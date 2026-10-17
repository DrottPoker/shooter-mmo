# Items And Inventory Delivery History

Status: Historical record. Every phase below is delivered. The rules live in
[Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md), and the
implemented behavior in the feature and architecture documents listed at the
end.

Last updated: 2026-10-17

## What This Was

This document was the dependency-ordered plan that built the item, inventory,
death loot, and world-actor foundation in sixteen phases. It also froze the
architecture that the implementation still follows:

- One transaction kernel inside GameService owns every durable item mutation:
  idempotency, lock order, authority, expected revisions, definition and policy
  resolution, slot rules, Bag aggregates, carry weight, audit, and revisions.
  HTTP routes, the SimulationWorker route, quest and insurance flows, and tests
  call the same kernel instead of their own SQL.
- SimulationWorker never touches PostgreSQL for items, and Unity never changes
  durable state without a server result.
- Neutral content under `content/` is compiled by one framework-neutral compiler
  that Unity, the command line, tests, and CI share. The Unity tools edit that
  content; Unity assets are never a second source of truth.
- NPCs and Mobs are world actors composed from typed capabilities, kept as
  SimulationWorker runtime state and rebuilt from content after a restart.

## Delivered Phases

| Phase | Delivered |
| --- | --- |
| 0 | Documentation and contract freeze |
| 1 | Item catalog content, the compiler, and the pure item rules |
| 2 | The Unity Item Catalog authoring and bake tool |
| 3 | Item schema, migrations, and character item-state bootstrap |
| 4 | The item read model and development fixtures |
| 5 | The core item transaction kernel |
| 6 | Policies, Bank, Secure Container tiers, and Recovery Storage APIs |
| 7 | Carry state and the shared encumbrance curve |
| 8 | The in-world item mutation boundary through SimulationWorker |
| 9 | The Unity inventory foundation |
| 10 | The death partition and durable player corpses |
| 11 | Concurrent corpse transfers and the atomic Bag swap |
| 12 | Scalable world actors, NPCs, Mobs, and the interaction foundation |
| 13 | Insurance and quest item lifecycle through NPC capabilities |
| 14 | Mob corpse variants |
| 15 | Performance, operations, and release hardening |

## Later Changes

- The October 2026 restructure renamed the `ShooterMmo` namespace root to
  `Mmorpg` and `AuthService` to `GameService`, and split GameService into
  modules. Click-to-move replaced sprint, the crosshair, and the `E` key.
- The [Items And Inventory Alignment Plan](ITEMS_INVENTORY_ALIGNMENT_PLAN.md)
  then aligned the system with the fantasy design: fantasy content on a tenfold
  weight scale, catalog retirement, the ammunition slot and blocked slots, Bank
  and Recovery Storage through NPC capabilities, and the removal of duplicated
  rules.

## Where Things Are Documented Now

- Rules and open questions:
  [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md).
- The item tables, the transaction kernel, and the lock order:
  [GameService Modules](GAME_SERVICE_MODULES.md).
- Routes, persistence, and the item and interaction error codes:
  [Service Features](SERVICE_FEATURES.md).
- Player-facing behavior: [Game Features](GAME_FEATURES.md).
- Catalog authoring: [Item Catalog Authoring](../content/items/README.md).
- Unity client state and panels:
  [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md).
- Commands and manual tests: [Local Development](LOCAL_DEVELOPMENT.md).

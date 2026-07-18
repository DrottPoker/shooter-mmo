# Documentation

Last updated: 2026-07-18

This directory is the documentation entry point for Shooter MMO. Each active
document has one clear responsibility so architecture, implemented behavior,
operations, and future design do not drift into duplicate descriptions.

## Start Here

1. [Project Overview](PROJECT_OVERVIEW.md) for a short explanation of the current
   project and its status.
2. [Project Architecture](PROJECT_ARCHITECTURE.md) for system boundaries, data
   ownership, canonical topology terminology, and communication flows.
3. [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md) for detailed Unity
   runtime structure and client flows.
4. [Service Features](SERVICE_FEATURES.md) for implemented backend and
   infrastructure behavior.
5. [Game Features](GAME_FEATURES.md) for implemented player-facing gameplay.
6. [Local Development](LOCAL_DEVELOPMENT.md) for setup, startup, and manual test
   instructions.
7. [Simulation Stress Testing](SIMULATION_STRESS_TESTING.md) for repeatable
   headless SimulationWorker hotspot, ramp, soak, and bottleneck measurement.
8. [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) for a long-running local
   bot population that can share the real shard with a Unity player.

## Product Direction

- [MVP Specification](MVP_SPEC.md) defines the working MVP scope and product
  decisions.
- [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md) is the canonical
  source of truth for the implemented scalable world-actor model, NPC and Mob
  distinction, composable NPC capabilities, visual spawn authoring, crosshair
  interaction, runtime ownership, and actor scaling rules.
- [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) is the
  current product and domain source of truth for planned items, slot inventory,
  Bags, Secure Container, carry weight, death loot, corpses, insurance, and
  Recovery Storage.
- [Items And Inventory Implementation Plan](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md)
  defines the dependency-ordered delivery phases, proposed persistence model,
  service boundaries, and verification gates for that design. Phases 1 through
  12 are implemented, including the item catalog, Unity authoring, PostgreSQL
  schema,
  transactional catalog mirror, character item-state bootstrap, authoritative
  reads, the race-safe transaction kernel, policy and quest-grant lifecycle,
  offline-safe account APIs, exact-session carry-state delivery, and shared
  SimulationWorker and Unity encumbrance. The exact-session in-world mutation
  boundary, persistent Unity inventory foundation, idempotent death partition,
  durable player corpses, worker restart restoration, and audited expiry are also
  complete, together with concurrent bidirectional corpse interaction. Phase 12
  adds deterministic actor content and tools, bounded worker actors, shared
  crosshair targeting, and authoritative NPC interaction. Phase 13 adds
  protocol version `13` and insurance plus quest item-lifecycle handlers on that
  interaction.
  Former Phases 12 through 14 are now Phases 13 through 15.
- [MMO Codex Project Brief](MMO_Codex_Project_Brief.md) contains the original
  project vision and broader historical design context. Current architecture and
  feature-specific design documents supersede conflicting details in that brief.

These product documents are working design references. The architecture and
feature documents describe what is actually implemented in the repository.

## Documentation Rules

- Update architecture documents when boundaries, ownership, dependencies, or
  major flows change.
- Update feature documents in the same change that implements or changes a
  feature.
- Document only implemented behavior as implemented. Keep planned work clearly
  marked as planned or deferred.
- Use World, Fleet, Node, SimulationWorker, SimulationAssignment, Shard, Zone,
  and Layer exactly as defined in `PROJECT_ARCHITECTURE.md`. Do not introduce a
  Realm or reuse World as a process name.
- Put commands and operator workflows in `LOCAL_DEVELOPMENT.md` instead of
  duplicating them across architecture and feature documents.
- Keep this index and the root `README.md` links current when documents are
  added, renamed, or removed.
- Only the current UI may be documented or implemented as temporary. All other
  systems must be treated as maintainable foundations and must not accumulate
  knowingly disposable architecture.
- Every Unity change must state whether manual Unity Editor work is required. If
  it is required, document exact steps and the expected result in
  `LOCAL_DEVELOPMENT.md`.

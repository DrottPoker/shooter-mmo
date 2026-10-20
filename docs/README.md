# Documentation

Last updated: 2026-10-20

This directory is the documentation entry point for MMORPG. Each active
document has one clear responsibility so architecture, implemented behavior,
operations, and future design do not drift into duplicate descriptions.

## Start Here

1. [Project Overview](PROJECT_OVERVIEW.md) for a short explanation of the current
   project and its status.
2. [Project Architecture](PROJECT_ARCHITECTURE.md) for system boundaries, data
   ownership, canonical topology terminology, and communication flows.
3. [GameService Modules](GAME_SERVICE_MODULES.md) for the GameService module
   projects, their contracts, table ownership, cross-module access rules, lock
   order, migration ownership, and architecture guard tests.
4. [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md) for the client
   assemblies and their dependency rules, Addressables content loading, and the
   detailed Unity runtime structure and client flows.
5. [Realtime Protocol](REALTIME_PROTOCOL.md) for the binary wire format, the
   protocol schema and its generator, the message descriptors, and the
   versioning rules.
6. [Service Features](SERVICE_FEATURES.md) for implemented backend and
   infrastructure behavior.
7. [Game Features](GAME_FEATURES.md) for implemented player-facing gameplay.
8. [Local Development](LOCAL_DEVELOPMENT.md) for setup, startup, manual test
   instructions, and the pull request workflow that waits for CI, merges, and
   cleans up.
9. [Monitoring](MONITORING.md) for the server metrics, logs, and traces, the
   local OpenTelemetry, Prometheus, Loki, Tempo, and Grafana stack, the
   dashboards and alerts, and what to watch during a playtest.
10. [Stack Stress Testing](STACK_STRESS_TESTING.md) for repeatable worker-only
    and full-stack GameService, PostgreSQL, and SimulationWorker load testing.
11. [Active Simulation Bots](ACTIVE_SIMULATION_BOTS.md) for a long-running local
    bot population that can share the real shard with a Unity player.
12. [Development Worlds](DEVELOPMENT_WORLDS.md) for the two development maps, their
    layouts, and the Unity rebuild and authoring workflow.
13. [Game Content](../content/README.md) for the authored content layout, its
    file and naming rules, the content compilers, and the generated data. The
    item authoring contract is in [Item Catalog Authoring](../content/items/README.md).
14. [Backend Tests](../tests/README.md) for the backend test projects, where a new
    test goes, test traits, per-test databases, CI jobs, golden files, and test
    access to internals.
15. [Art Pipeline](ART_PIPELINE.md) for 3D models from Blender to Unity: the
    asset layout, the review stages and approvals, the export and import rules,
    the modeling conventions, the human base characters and their scripted
    build, and the Blender MCP server for live work.

## Foundation Hardening

- [Foundation Hardening Plan](FOUNDATION_HARDENING_PLAN.md) records the verified
  baseline, every finding from the October 2026 audit of the existing
  foundation, open decisions, and the fix order. It is completed before new
  gameplay work starts.
- [Items And Inventory Alignment Plan](ITEMS_INVENTORY_ALIGNMENT_PLAN.md)
  records the October 2026 audit of the item and inventory system against the
  fantasy design, the product decisions it settled, and the fixes.

## Product Direction

- [MVP Specification](MVP_SPEC.md) defines the working MVP scope and product
  decisions.
- [Vendor And Gathering Plan](VENDOR_AND_GATHERING_PLAN.md) records the
  implemented vendor, currency, and gathering MVP slice: architecture, product
  decisions, implementation order, and where the implementation differs.
- [Combat And Setting Design](COMBAT_AND_SETTING_DESIGN.md) is the product
  source of truth for the fantasy setting, character levels and talents,
  click-to-move movement and camera, the combat triangle, damage types and
  resistances, weapons, ammunition, magic, mana, threat, healing, PvP combat,
  and professions. It replaces the earlier modern third-person shooter
  direction. Its click-to-move movement, orbit camera, and first combat slice
  are implemented; the rest is not implemented yet.
- [NPC And Mob System Design](NPC_AND_MOB_SYSTEM_DESIGN.md) is the canonical
  source of truth for the implemented scalable world-actor model, NPC and Mob
  distinction, composable NPC capabilities, visual spawn authoring, click
  interaction, runtime ownership, and actor scaling rules.
- [Inventory And Death Loot Design](INVENTORY_AND_DEATH_LOOT_DESIGN.md) is the
  product and domain source of truth for items, equipment, slot inventory, Bags,
  the Secure Container, carry weight, death loot, corpses, insurance, and
  Recovery Storage, split into decided, proposed, and open rules.
- [Combat And Mob Slice Plan](COMBAT_AND_MOB_SLICE_PLAN.md) records the
  implemented MVP slice for target-based combat and the first Mob: its product
  decisions, the architecture of combat state, attack timing, ammunition
  batching, death events, Mob AI, protocol, and client presentation, the
  implementation order, where the implementation differs, and the acceptance
  run.
- [Items And Inventory Delivery History](ITEMS_INVENTORY_IMPLEMENTATION_PLAN.md)
  records the delivered phases of the item and world-actor foundation. The
  current implementation status is in [Project Overview](PROJECT_OVERVIEW.md).
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

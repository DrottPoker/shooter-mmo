# Documentation

Last updated: 2026-07-13

This directory is the documentation entry point for Shooter MMO. Each active
document has one clear responsibility so architecture, implemented behavior,
operations, and future design do not drift into duplicate descriptions.

## Start Here

1. [Project Overview](PROJECT_OVERVIEW.md) for a short explanation of the current
   project and its status.
2. [Project Architecture](PROJECT_ARCHITECTURE.md) for system boundaries, data
   ownership, and communication flows.
3. [Unity Client Architecture](UNITY_CLIENT_ARCHITECTURE.md) for detailed Unity
   runtime structure and client flows.
4. [Service Features](SERVICE_FEATURES.md) for implemented backend and
   infrastructure behavior.
5. [Game Features](GAME_FEATURES.md) for implemented player-facing gameplay.
6. [Local Development](LOCAL_DEVELOPMENT.md) for setup, startup, and manual test
   instructions.

## Product Direction

- [MVP Specification](MVP_SPEC.md) defines the working MVP scope and product
  decisions.
- [MMO Codex Project Brief](MMO_Codex_Project_Brief.md) contains the original
  project vision and broader design context.

These product documents are working design references. The architecture and
feature documents describe what is actually implemented in the repository.

## Documentation Rules

- Update architecture documents when boundaries, ownership, dependencies, or
  major flows change.
- Update feature documents in the same change that implements or changes a
  feature.
- Document only implemented behavior as implemented. Keep planned work clearly
  marked as planned or deferred.
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

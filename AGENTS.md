# Agent Instructions

Follow these rules for all future work in this repository.

- Do not use em dashes in prose, documentation, comments, or code comments.
- Write all code, code comments, identifiers, commit messages, and technical
  documentation in English.
- Keep documentation updated whenever behavior, configuration, architecture, or
  user-facing workflows change.
- Always include how the user can manually test delivered changes, including
  the relevant commands and expected result.
- Prefer small, focused changes that match the existing project structure.
- Verify backend changes with lint and compile checks when practical.
- Verify frontend changes with typecheck when practical.
- Only UI may use explicitly temporary implementations while the custom UI is
  being designed. Architecture, networking, state management, gameplay systems,
  service code, and tooling must be implemented as maintainable foundations from
  the start.
- When a change requires Unity Editor actions, always list the exact manual steps
  the user must perform and the expected result. Explicitly state when no manual
  Unity Editor steps are required.

## Branches

- `main` and `development` are the only long-lived branches. `main` is never
  committed to directly; it is only fast-forwarded to `development`.
- Start every new task on a new branch created from the latest `development`.
  A worktree branch is the task branch, so base it on `development`, not `main`.
- When the task is finished and verified, merge its branch into `development`
  through a pull request whose CI passes, then delete the branch locally and on
  GitHub. This is standing permission to commit, push, open the pull request,
  merge it, and delete the branch for that task. No other branch is left behind.

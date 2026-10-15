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

# Agent Instructions

Follow these rules for all future work in this repository.

- Do not use em dashes in prose, documentation, comments, or code comments.
- Write all code, code comments, identifiers, commit messages, and technical
  documentation in English.
- Keep documentation updated whenever behavior, configuration, architecture, or
  user-facing workflows change, following the documentation map and rules in
  `docs/README.md`: every fact lives in the one document that owns it.
- Always include how the user can manually test delivered changes, including
  the relevant commands and expected result. Lasting manual checks belong in
  `docs/MANUAL_TESTING.md`.
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

## Verification

- Check at three levels, as `docs/LOCAL_DEVELOPMENT.md` describes under Checks
  For A Change. While changing code, run only the tests that cover it: one .NET
  test project, narrowed with `--filter` when that is faster, or
  `tools/Run-UnityEditorTests.ps1 -Filter <fixture>`. Before every push, run
  `tools/Test-Changes.ps1`, which runs the checks that the changes of the branch
  can affect. CI runs the full quality gate for every pull request, so run the
  full gate locally, with `tools/Test-Changes.ps1 -All`, only when the user asks
  for it.
- Never rerun the full gate or a whole suite after each small change.
- When CI fails on something that `tools/Test-Changes.ps1` did not run, fix it
  and add the missing rule to that script in the same task.
- Run Unity tests in a running Editor with `tools/Run-UnityEditorTests.ps1`,
  not with hand-written loops around `run_tests` and `test_status`.

## Branches

- `main` and `development` are the only long-lived branches. `main` is never
  committed to directly; it is only fast-forwarded to `development`.
- Start every new task on a new branch created from the latest `development`.
  A worktree branch is the task branch, so base it on `development`, not `main`.
- When the task is finished and verified, merge its branch into `development`
  through a pull request whose CI passes, then delete the branch locally and on
  GitHub. This is standing permission to commit, push, open the pull request,
  wait for CI, fix what CI and review find, merge it, and delete the branch for
  that task. No other branch is left behind.
- CI runs for pull requests, not for pushes to task branches, so open the pull
  request to get CI. The ruleset `development CI gate` blocks the merge until
  every CI job passes, including the Unity quality gate on the self-hosted
  runner; wait for it and never disable the ruleset to get a merge through.
- Wait with `tools/Wait-PullRequest.ps1` and merge with
  `tools/Complete-PullRequest.ps1`, as `docs/LOCAL_DEVELOPMENT.md` describes
  under Pull Request Workflow. Before the merge, every valid finding of the
  Codex review bot is fixed and every review thread is resolved.
- After every merge into `development`, fast-forward the local `development`
  branch in the main checkout with `git pull --ff-only origin development`, so
  the user always has the latest `development` locally. Never reset or discard
  local work for this: if uncommitted changes block the pull, show them to the
  user and ask before stashing them under a descriptive name.

## Task Workflow

- Carry every task from start to finish without stopping: implement, verify,
  open the pull request, wait for CI and the review bot, fix what they find,
  merge, clean up, and report. Fixing CI failures and valid review findings is
  part of the task and never needs the user's approval. Stop to ask the user
  only about unclear product, implementation, or design decisions, or before an
  action that these rules do not already permit.
- Fix related bugs that come up during a task in the same task, in its pull
  request or in a follow-up pull request from the same task. Only large and
  independent work, such as a redesign of another system, becomes a separate
  task, and the final summary says why.
- Do not split a task into parallel or follow-up agent sessions unless the user
  asks for it. When work was split anyway, the session the user talks to
  collects every part and reports all of it in one summary.
- End every task with a summary for the user, in the conversation where the
  user asked for the work: what was implemented and why, the areas and files
  that changed, every pull request with its merge commit, the tests and checks
  that ran and their results, related bugs fixed on the way, anything left
  open, how to test the change manually with commands and the expected result,
  whether Unity Editor steps are needed, and the cleanup state.
- Leave the environment clean for the next task, like a kitchen after cooking:
  the task branch is deleted locally and on GitHub, `development` is
  fast-forwarded in the main checkout, and no worktree, stash, container,
  background process, or temporary file that the task created is left behind.

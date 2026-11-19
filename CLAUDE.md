@AGENTS.md

# Claude Code Notes

The shared repository rules are in `AGENTS.md`, imported above. The
documentation map and documentation rules are in `docs/README.md`. This file
only adds the workflow notes that a coding agent needs to carry a task through
and verify its own work.

## Task Workflow In The Claude App

Victor has given standing approval for the whole loop in `AGENTS.md` under
Task Workflow. These notes override the app's defaults where they differ.

- The loop itself is global, so it holds in every project of Victor's: Git in
  `~/.claude/CLAUDE.md` and the `ship` skill, which never asks whether to wait,
  fix, merge, or clean up and never offers Auto-fix. This project uses its own
  `tools/Wait-PullRequest.ps1` and `tools/Complete-PullRequest.ps1` instead of
  the skill's copies, because they know the `development CI gate` ruleset. Act
  on their exit codes as `docs/LOCAL_DEVELOPMENT.md` describes under Pull
  Request Workflow.
- Do not start side sessions (`start_session`) for related fixes or follow-ups,
  and do not split an accepted plan into parallel sessions. Do the work in this
  session. Propose a separate session only for large, independent work, and say
  why in the summary.
- The wrap-up recap is too short for the task summary. When nothing waits on
  Victor, call `wrap_up` without a question and write the full summary from
  `AGENTS.md` in the closing message after it.
- Archive finished sessions at the start and at the end of every task: list
  this repository's sessions with `ccd_session_mgmt` `list_sessions` and
  archive every other session that is not running and whose pull request is
  merged or closed, or that has no pull request and a clean worktree whose
  commit is in `origin/development`. Archiving hands the worktree back to the
  app's reuse pool, which the app reuses or removes itself. Never archive a
  session that is running or waits for Victor, or the session that started one,
  because archiving a session also archives its finished side sessions. Never
  archive a session that is less than 48 hours old, counted from its last
  activity (`lastActivityAt` in `list_sessions`), so Victor can still read and
  continue recent chats. A session cannot remove its own worktree while it
  runs, so a later task archives it, and the finished session keeps its summary
  visible until then. When the app blocks an archive, name the sessions in the
  summary so Victor can archive them from the sidebar.

## Delegating To Subagents

The delegation rules are global, so they hold in every project of Victor's:
Delegation in `~/.claude/CLAUDE.md`, and the `delegate` skill with the roles
`implementer`, `surveyor`, and `reviewer`, the brief template, and the checks
after each agent. The session Victor talks to owns the task and answers for
what its subagents do and leave behind. A subagent runs inside the session, in
the same checkout, so this is not the splitting into sessions that `AGENTS.md`
forbids. This project adds:

- The subtle parts that the session does itself are simulation determinism,
  protocol changes, a Foundation contract, hard debugging, and art that Victor
  approves stage by stage.
- The brief names the focused tests to run and the repository rules that
  apply.
- An agent may run tests in the checkout, so never start
  `tools/Test-Changes.ps1` while an agent still works there. After the agent,
  read the diff itself, then run `tools/Test-Changes.ps1` in the session before
  the push.
- Victor picks the model and the effort level of the session itself.

## Verification

- Check at the three levels in `AGENTS.md` under Verification. Before a push,
  run `tools/Test-Changes.ps1` from the checkout of the task; `-Plan` shows what
  it would run and why. The build must stay at zero warnings.
- `tools/Test-Changes.ps1` takes `MMORPG_TEST_POSTGRES` and `MMORPG_TEST_REDIS`
  from the `.env` of the main checkout when they are not set, starts
  `docker-compose.test.yml` when an integration test is selected and the
  containers do not run, and stops the containers it started. Never print
  `.env` values. Integration tests take about one minute with `Host=127.0.0.1`;
  `Host=localhost` makes every new connection on Windows wait about two seconds.
  Every test gets its own database below `<base>_<project>`, so test projects run
  in parallel; never start two runs of the same test project at once.
- Focused runs: `dotnet test tests/Server/<Project> --configuration Release` for
  one test project, with `--filter "FullyQualifiedName~<Class>"` for one class,
  or with `--filter "Category!=Integration"` without containers.
- The full gate is the command list in `docs/LOCAL_DEVELOPMENT.md` under
  Repository Quality Checks, or `tools/Test-Changes.ps1 -All` in one command. CI
  runs it for every pull request.
- New tests go into the test project of the module they test. The layout,
  traits, database rules, and internal access policy are in `tests/README.md`.
- Local stack for manual or end-to-end tests: `docker compose up -d --wait`,
  then `dotnet run --project src/Server/GameService` and
  `dotnet run --project src/Server/SimulationWorker` as background processes. Stop both
  processes when finished. The default worker runs Development World 2 on
  `local-shard-1`; add `--launch-profile StressArena` for a second worker that
  runs the Development World 1 stress arena on `local-shard-2` and UDP `27016`.
  The manual checks of every feature are in `docs/MANUAL_TESTING.md`.

## Unity Editor

The Unity project in `src/Client` includes the Pipeline
package, so a running Editor can be driven with the `unity` CLI. Setup and
security rules are in `docs/LOCAL_DEVELOPMENT.md` under Unity Editor Automation
With Unity CLI.

- Run `unity command editor_status --project-path <checkout>/src/Client` first,
  never `unity status`, which makes every running Editor log an error and so
  fails the CI runner's Unity gate when it runs at that moment. When an Editor
  is connected, change scenes, prefabs, and assets through `unity command ...`
  and never hand-edit `.unity`, `.prefab`, or `.asset` YAML.
- Check compile state with `unity command console_status` after C# changes.
- Unity tests: `tools/Run-UnityEditorTests.ps1` runs both suites in the Editor
  that has the script's own checkout open, and `-Mode EditMode -Filter <fixture>`
  runs one fixture while iterating. It leaves Play Mode, refreshes and compiles
  first, fails at once on compile errors, and always ends with a report, so
  never write polling loops around `run_tests` and `test_status`.
- `unity command eval_file --file <path>` runs a C# file. Keep such scripts in
  the session scratchpad, not in the repository.
- `capture_game_view` and `capture_scene_view` always save below `Assets/`.
  Move the image out and delete the created folder and `.meta` file afterwards,
  so no screenshot reaches Git.
- The headless `tools/Run-UnityTests.ps1` cannot run while the Editor has the
  project open. Use the live Editor commands above instead.
- Exit Play Mode with `unity command editor_stop` before finishing.
- Look at every change that a player can see or feel, as `AGENTS.md` requires
  under Verification, also when a subagent made it. When `editor_status`
  reports no Editor for the checkout, open one with
  `unity open <checkout>/src/Client`, start the local stack, enter Play Mode
  from `LoginMenu`, drive the real flow with `eval_file`, and read each
  `capture_game_view` image before reporting. Close an Editor that the session
  opened, with `UnityEditor.EditorApplication.Exit(0)` through `eval_file`, and
  leave an Editor that Victor had open as it was.
- An Addressables content build started through `eval_file` blocks the Editor
  on a modal dialog when an open scene is unsaved, and once per Library on the
  "Addressables Build Report" question. Open a saved scene first and answer the
  question in the Editor, or build from the Addressables Groups window.

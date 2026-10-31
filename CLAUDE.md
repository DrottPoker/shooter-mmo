@AGENTS.md

# Claude Code Notes

The shared repository rules are in `AGENTS.md`, imported above. The
documentation map and documentation rules are in `docs/README.md`. This file
only adds the workflow notes that a coding agent needs to carry a task through
and verify its own work.

## Task Workflow In The Claude App

Victor has given standing approval for the whole loop in `AGENTS.md` under
Task Workflow. These notes override the app's defaults where they differ.

- Never offer Auto-fix, and never ask whether to wait for CI, fix CI, address
  review findings, merge, or clean up. Never end a turn with such a question or
  button in the wrap-up box. Auto-fix wakes a session only on failures, never
  when CI turns green, so a session that relied on it stopped and waited for
  Victor.
- After `gh pr create`, bind the pull request with the ccd_pr tools so the app
  shows it, then run `tools/Wait-PullRequest.ps1 -Number <number>` once as a
  background command (`run_in_background`). The harness wakes the session when
  the script exits. Act on its exit code as `docs/LOCAL_DEVELOPMENT.md`
  describes under Pull Request Workflow, start it again after every push, and
  do not poll CI in any other way. On exit code 0, run
  `tools/Complete-PullRequest.ps1`.
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

- Run `unity status` first. When an Editor is connected, change scenes,
  prefabs, and assets through `unity command ...` and never hand-edit `.unity`,
  `.prefab`, or `.asset` YAML.
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
- An Addressables content build started through `eval_file` blocks the Editor
  on a modal dialog when an open scene is unsaved, and once per Library on the
  "Addressables Build Report" question. Open a saved scene first and answer the
  question in the Editor, or build from the Addressables Groups window.

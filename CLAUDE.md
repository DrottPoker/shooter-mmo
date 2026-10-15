@AGENTS.md

# Claude Code Notes

The shared repository rules are in `AGENTS.md`, imported above. The
documentation map and documentation rules are in `docs/README.md`. This file
only adds the workflow notes that a coding agent needs to verify its own work.

## Verification

- Backend quality gate: the command list in `README.md` under Standard Quality
  Checks. Run it from the repository root. The build must stay at zero warnings.
- Integration tests: start `docker-compose.test.yml` and set
  `MMORPG_TEST_POSTGRES` and `MMORPG_TEST_REDIS` from `.env`, as shown
  in `README.md`. A full run takes about six to seven minutes, so run it in the
  background. Never print `.env` values.
- Local stack for manual or end-to-end tests: `docker compose up -d --wait`,
  then `dotnet run --project src/Server/GameService` and
  `dotnet run --project src/Server/SimulationWorker` as background processes. Stop both
  processes when finished.

## Unity Editor

The Unity project in `src/Client` includes the Pipeline
package, so a running Editor can be driven with the `unity` CLI. Setup and
security rules are in `docs/LOCAL_DEVELOPMENT.md` under Unity Editor Automation
With Unity CLI.

- Run `unity status` first. When an Editor is connected, change scenes,
  prefabs, and assets through `unity command ...` and never hand-edit `.unity`,
  `.prefab`, or `.asset` YAML.
- Check compile state with `unity command console_status` after C# changes.
- EditMode tests: `unity command run_tests --mode editor --timeout 580`.
- PlayMode tests: `unity command run_tests --mode playmode --async_tests`, then
  poll `unity command test_status` until `status` is `completed`.
- `unity command eval_file --file <path>` runs a C# file. Keep such scripts in
  the session scratchpad, not in the repository.
- `capture_game_view` and `capture_scene_view` always save below `Assets/`.
  Move the image out and delete the created folder and `.meta` file afterwards,
  so no screenshot reaches Git.
- The headless `tools/Run-UnityTests.ps1` cannot run while the Editor has the
  project open. Use the live Editor commands above instead.
- Exit Play Mode with `unity command editor_stop` before finishing.

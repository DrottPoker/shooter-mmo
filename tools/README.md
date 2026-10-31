# Tools

Scripts and programs that check, build, and operate the project. Run them from the repository
root. The PowerShell scripts work in Windows PowerShell 5.1, and each explains its parameters in
its header comment. The C# tools are projects of `Mmorpg.slnx` and run with
`dotnet run --project tools/<Tool>`.

## Daily Workflow

| Tool | Use |
| --- | --- |
| `Test-Changes.ps1` | Runs the checks that the changes of the branch can affect; run it before every push. `-Plan` shows what it would run, `-All` runs the full gate. See [Checks For A Change](../docs/LOCAL_DEVELOPMENT.md#checks-for-a-change) |
| `Run-UnityEditorTests.ps1` | Runs the Unity EditMode and PlayMode tests in the open Unity Editor of this checkout; `-Mode EditMode -Filter <Fixture>` runs one fixture |
| `Run-UnityTests.ps1` | Runs both Unity suites headless when no Editor has the project open; CI uses it |
| `Wait-PullRequest.ps1` | Waits until a pull request is ready to merge or needs work. See [Pull Request Workflow](../docs/LOCAL_DEVELOPMENT.md#pull-request-workflow) |
| `Complete-PullRequest.ps1` | Merges a ready pull request into `development` and cleans up its branches |
| `PullRequestCommon.ps1` | Shared helpers of the two pull request scripts |

## Quality Gate Checks

These run in CI and, when the changes call for them, in `Test-Changes.ps1`.

| Tool | Checks |
| --- | --- |
| `Verify-DependencyPolicy.ps1` | Exact package versions, one xUnit version, the Unity lock file, and aligned LiteNetLib versions |
| `Verify-Observability.ps1` | The monitoring stack configuration, alert rule tests, and dashboards. See [Monitoring](../docs/MONITORING.md) |
| `Verify-TestResults.ps1` | That every test in the TRX results passed, so a skipped test fails the build |
| `art/Verify-ArtAssets.ps1` | That every art asset is approved and every committed model and texture is its recorded export |

## Content And Protocol Compilers

The content compilers turn the authored files in `content/` into the deterministic data in
`src/Shared/WorldData/Generated`, in this order, and `--verify` checks that the committed data
is current. See [Game Content](../content/README.md).

| Tool | Does |
| --- | --- |
| `ItemCatalogCompiler/` | Compiles the item catalog. See [Item Catalog Authoring](../content/items/README.md) |
| `WorldActorCompiler/` | Compiles the actor definitions and the actor spawns of every World |
| `WorldCollisionCompiler/` | Compiles the collision chunks of every World, including the resource nodes that block movement |
| `ContentPipeline/` | The library the compilers share, also used by the Unity content tools |
| `ProtocolGenerator/` | Generates the realtime protocol code from its schema. See [Realtime Protocol](../docs/REALTIME_PROTOCOL.md) |

## Load And Bot Tools

| Tool | Does |
| --- | --- |
| `StackStressGenerator/` | Finite stress benchmark of a SimulationWorker or the full stack. See [Stack Stress Testing](../docs/STACK_STRESS_TESTING.md) |
| `ActiveSimulationBots/` | Long-running bot population that shares a local shard with Unity players. See [Active Simulation Bots](../docs/ACTIVE_SIMULATION_BOTS.md) |
| `SimulationBotClient/` | The headless UDP client library both bot tools use |

## Art Pipeline

`art/` holds the scripts that take a 3D model from Blender to Unity, and `art/blender/` the
Python code they run inside Blender, with its tests in `art/blender/tests`. When and how to use
each script is in [Art Pipeline](../docs/ART_PIPELINE.md).

| Tool | Does |
| --- | --- |
| `art/New-ArtAsset.ps1` | Creates a new asset: its folder, `asset.json`, and first `.blend` file |
| `art/Render-ArtPreview.ps1` | Renders the review sheet of an asset |
| `art/Approve-ArtAsset.ps1` | Records the approval of one review stage for the current `.blend` file |
| `art/Export-ArtAsset.ps1` | Exports approved assets to Unity and records the export |
| `art/Verify-ArtAssets.ps1` | Checks every asset without Blender |
| `art/Sync-HumanBase.ps1` | Updates the human base characters after an edit in Blender |
| `art/Sync-AnimationLibrary.ps1` | Builds or updates an animation library from its rig |
| `art/Sync-PaintAsset.ps1` | Builds or updates a paint layer on its bodies |
| `art/Sync-CageAsset.ps1` | Fits the garment cage to its bodies and checks it |
| `art/Sync-GarmentAsset.ps1` | Fits a garment template to the bodies of its cage and checks it |
| `art/Sync-TextureAsset.ps1` | Brings a garment texture up to date with its template and checks it |
| `art/Set-TextureDyes.ps1` | Makes a garment texture dyeable or sets its dyes again |
| `art/Test-PaintProjection.ps1` | Measures how a paint layer moves to a body of another topology |
| `art/ArtPipelineCommon.ps1` | Shared helpers of the art scripts |

## Self-Hosted Runner

`ci/` holds the setup of the self-hosted Windows runner that runs the Unity tests in CI, described
in [Unity CI Runner](../docs/LOCAL_DEVELOPMENT.md#unity-ci-runner).

| Tool | Does |
| --- | --- |
| `ci/Assert-TrustedRunnerJob.ps1` | Job-started hook that refuses jobs from forks; the runner runs an installed copy |
| `ci/Register-UnityRunnerTask.ps1` | Registers the scheduled task that keeps the runner running |

# Compiles all game content in the order the compilers need, so one command brings every
# compiled file up to date after a content change, a format change, or a merge conflict in a
# compiled file. The order is items, actors, collision, quests, and the hand-written text
# tables, then the test fixtures: the item fixture catalog and the quest fixture catalog.
#
# -Verify writes nothing and fails when a compiled file is stale, which is what CI checks.
# Without it the script ends by listing the compiled files that changed.
#
# The Unity-owned files are not compiled here: the item presentation catalog and each World's
# collision.json come from the Unity Editor. The realtime protocol has its own generator.
#
# Exit codes: 0 everything compiled or verified, 1 a step failed.
param(
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Windows PowerShell 5.1 turns native stderr into errors under "Stop" once it is redirected, so
# native commands run with errors suspended and their exit code decides.
function Invoke-Native([string]$FilePath, [string[]]$ArgumentList) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @ArgumentList | Out-Host
        $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Invoke-Step([string]$Name, [string[]]$ArgumentList) {
    Write-Host ""
    Write-Host "== $Name"
    if ((Invoke-Native "dotnet" $ArgumentList) -ne 0) {
        Write-Host ""
        Write-Host "Failed: $Name"
        exit 1
    }
}

function Invoke-Compiler([string]$Name, [string]$Tool, [string[]]$ToolArguments = @()) {
    $arguments = @("run", "--project", "tools/$Tool", "--configuration", "Release", "--no-build", "--") + $ToolArguments
    if ($Verify) { $arguments += "--verify" }
    Invoke-Step $Name $arguments
}

$compilers = @(
    "ItemCatalogCompiler",
    "WorldActorCompiler",
    "WorldCollisionCompiler",
    "QuestCatalogCompiler",
    "TextTableCompiler"
)
$fixtureTests = "tests/Tools/Mmorpg.Tools.Tests"
$fixtureFilter = "FullyQualifiedName~CompiledFixtureCatalogMatchesItsAuthoring" +
    "|FullyQualifiedName~CompiledFixtureQuestCatalogMatchesItsAuthoring" +
    "|FullyQualifiedName~FixtureQuestTextTableMatchesItsAuthoring"

Push-Location $repositoryRoot
$previousUpdateGoldens = $env:MMORPG_UPDATE_GOLDENS
try {
    foreach ($compiler in $compilers) {
        Invoke-Step "Build $compiler" @("build", "tools/$compiler", "--configuration", "Release", "--nologo", "--verbosity", "quiet", "-clp:NoSummary")
    }
    Invoke-Step "Build the fixture tests" @("build", $fixtureTests, "--configuration", "Release", "--nologo", "--verbosity", "quiet", "-clp:NoSummary")

    Invoke-Compiler "Item catalog" "ItemCatalogCompiler"
    Invoke-Compiler "World actors" "WorldActorCompiler"
    Invoke-Compiler "World collision" "WorldCollisionCompiler"
    Invoke-Compiler "Quest catalog" "QuestCatalogCompiler"
    Invoke-Compiler "Text tables" "TextTableCompiler"
    Invoke-Compiler "Item fixture catalog" "ItemCatalogCompiler" @(
        "--content-root", "tests/Fixtures/ItemCatalog/content",
        "--generated-root", "tests/Fixtures/ItemCatalog/generated")

    # The quest fixture compiles against the fixture items and the fixture World layout, which
    # the test kit knows, so its tests write it.
    if ($Verify) { $env:MMORPG_UPDATE_GOLDENS = $null } else { $env:MMORPG_UPDATE_GOLDENS = "1" }
    Invoke-Step "Quest fixture catalog" @(
        "test", $fixtureTests, "--configuration", "Release", "--no-build", "--nologo",
        "--filter", $fixtureFilter)

    Write-Host ""
    if ($Verify) {
        Write-Host "All compiled content is current."
    }
    else {
        $changed = @(& git status --short -- "src/Shared/WorldData/Generated" "tests/Fixtures")
        if ($changed.Count -eq 0) {
            Write-Host "All content compiled. No compiled file changed."
        }
        else {
            Write-Host "All content compiled. Changed compiled files:"
            $changed | ForEach-Object { Write-Host $_ }
        }
    }
}
finally {
    $env:MMORPG_UPDATE_GOLDENS = $previousUpdateGoldens
    Pop-Location
}

# Runs the Unity EditMode and PlayMode tests headless and fails unless every selected test
# passed. The CI Unity gate runs it as it is. By default it leaves out two categories: Graphics,
# whose tests draw and need a graphics device, and LiveStack, whose tests need the local stack;
# tools/Test-LiveStack.ps1 starts that stack and runs this script for the LiveStack tests alone.
# -Platform StandaloneWindows64 builds a Windows test player with the committed IL2CPP settings,
# runs it, and runs the Player category in it; the player opens a window while it runs.
# Unity opens a project only once, so the script refuses to run while an Editor or another
# Unity process has it open; tools/Run-UnityEditorTests.ps1 runs the tests in an open Editor.
param(
    [string]$UnityEditorPath = $env:UNITY_EDITOR_PATH,
    [string]$UnityCliPath = $env:UNITY_CLI_PATH,
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\src\Client"),
    [string]$ResultsPath = (Join-Path $PSScriptRoot "..\TestResults\Unity"),
    # The test platforms to run, in this order.
    [ValidateSet("EditMode", "PlayMode", "StandaloneWindows64")]
    [string[]]$Platform = @("EditMode", "PlayMode"),
    # Unity's -testCategory: categories separated by semicolons, an exclamation mark excludes one.
    # The default is !Graphics;!LiveStack in the Editor and Player in a test player.
    [string]$TestCategory,
    # Checks that Unity is installed and the project is closed, and runs no test.
    [switch]$Preflight
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "UnityCommon.ps1")

# The Player category holds the checks that must pass in the runtime of a built player.
function Get-TestCategory([string]$PlatformName) {
    if (-not [string]::IsNullOrWhiteSpace($TestCategory)) {
        return $TestCategory
    }

    if ($PlatformName -eq "StandaloneWindows64") {
        return "Player"
    }

    return "!Graphics;!LiveStack"
}

$requiredVersion = Get-UnityRequiredVersion $ProjectPath

# An explicitly configured Editor path always wins. The Unity CLI is only picked
# automatically when neither an Editor path nor a CLI path was given.
$explicitEditorPath = -not [string]::IsNullOrWhiteSpace($UnityEditorPath)
$explicitCliPath = -not [string]::IsNullOrWhiteSpace($UnityCliPath)

if (-not $explicitEditorPath -and -not $explicitCliPath -and
    ($IsWindows -or $env:OS -eq "Windows_NT")) {
    $UnityCliPath = Join-Path $env:LOCALAPPDATA "Unity\bin\unity.exe"
}

if ($explicitEditorPath -and -not $explicitCliPath) {
    $UnityCliPath = $null
}

$UnityEditorPath = Get-UnityEditorPath $UnityEditorPath $requiredVersion

$useUnityCli = -not [string]::IsNullOrWhiteSpace($UnityCliPath) -and
    (Test-Path -LiteralPath $UnityCliPath)

if (-not $useUnityCli -and
    ([string]::IsNullOrWhiteSpace($UnityEditorPath) -or -not (Test-Path -LiteralPath $UnityEditorPath))) {
    throw "Unity Editor $requiredVersion was not found. Set UNITY_EDITOR_PATH to the Unity executable."
}

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path

if (Test-UnityProjectOpen $resolvedProjectPath) {
    throw "A Unity Editor or another Unity process has '$resolvedProjectPath' open. Close it, or run the tests in that Editor with tools/Run-UnityEditorTests.ps1."
}

if ($Preflight) {
    Write-Output "Unity $requiredVersion can run the tests of '$resolvedProjectPath'."
    return
}

New-Item -ItemType Directory -Force -Path $ResultsPath | Out-Null

# Unity rewrites some tracked files of its own during a run; each run puts them back.
$repositoryRoot = (& git -C $resolvedProjectPath rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw "'$resolvedProjectPath' is not inside a Git working tree."
}

$repositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot).Path
$workingTree = Get-WorkingTreeState $repositoryRoot

foreach ($platformName in $Platform) {
    $resultFile = Join-Path $ResultsPath "$platformName-results.xml"
    $logFile = Join-Path $ResultsPath "$platformName-unity.log"
    $cliLogFile = Join-Path $ResultsPath "$platformName-cli.log"
    $category = Get-TestCategory $platformName
    $isPlayer = $platformName -eq "StandaloneWindows64"
    Remove-Item -LiteralPath $resultFile, $logFile, $cliLogFile `
        -Force -ErrorAction SilentlyContinue
    # The CLI's test command runs only EditMode and PlayMode and its run command quits before a
    # test run ends, so a player run always starts the Editor itself.
    $useCliForPlatform = $useUnityCli -and -not $isPlayer
    if ($isPlayer -and
        ([string]::IsNullOrWhiteSpace($UnityEditorPath) -or -not (Test-Path -LiteralPath $UnityEditorPath))) {
        throw "Unity Editor $requiredVersion was not found for the player run. Set UNITY_EDITOR_PATH to the Unity executable."
    }

    if ($useCliForPlatform) {
        $arguments = @(
            "--non-interactive",
            "--no-banner",
            "test", $resolvedProjectPath,
            "--mode", $platformName,
            "--output", $resultFile,
            "--editor-version", $requiredVersion,
            "--timeout", "900",
            "--",
            "-nographics",
            "-testCategory", $category,
            "-logFile", $logFile
        )
    }
    else {
        $arguments = @(
            "-batchmode",
            "-nographics",
            "-projectPath", $resolvedProjectPath,
            "-runTests",
            "-testPlatform", $platformName,
            "-testResults", $resultFile,
            "-testCategory", $category,
            "-logFile", $logFile
        )
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = if ($useCliForPlatform) { $UnityCliPath } else { $UnityEditorPath }
    $startInfo.Arguments = ($arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $useCliForPlatform
    $startInfo.RedirectStandardError = $useCliForPlatform
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $started = $false
    $runStarted = Get-Date
    try {
        if (-not $process.Start()) {
            throw "Unity $platformName tests could not start the Unity process."
        }

        $started = $true
        if ($useCliForPlatform) {
            $standardOutput = $process.StandardOutput.ReadToEndAsync()
            $standardError = $process.StandardError.ReadToEndAsync()
        }

        # Waits in slices, so an interrupted script reaches the cleanup below.
        while (-not $process.WaitForExit(500)) {
        }

        $exitCode = $process.ExitCode
        if ($useCliForPlatform) {
            $cliLog = $standardOutput.GetAwaiter().GetResult() +
                $standardError.GetAwaiter().GetResult()
            [System.IO.File]::WriteAllText($cliLogFile, $cliLog)
        }
    }
    finally {
        # An interrupted run takes the Unity it started with it, and the test scene that Unity's
        # PlayMode runner made in Assets and deletes only at the end of a run.
        if ($started -and -not $process.HasExited) {
            & taskkill.exe /PID $process.Id /T /F | Out-Null
            $null = $process.WaitForExit(10000)
            Get-ChildItem -LiteralPath (Join-Path $resolvedProjectPath "Assets") -Filter "InitTestScene*.unity*" -File |
                Where-Object { $_.CreationTime -ge $runStarted } |
                Remove-Item -Force -ErrorAction SilentlyContinue
        }

        $process.Dispose()
        foreach ($line in @(Restore-UnityOwnedFiles $workingTree $repositoryRoot)) {
            Write-Output $line
        }
    }

    if ($exitCode -ne 0) {
        if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile -Tail 100
        }

        $cliHint = if ($useCliForPlatform) { " Inspect '$cliLogFile' locally." } else { "" }
        throw "Unity $platformName tests failed with exit code $exitCode.$cliHint"
    }

    if (-not (Test-Path -LiteralPath $resultFile)) {
        throw "Unity $platformName tests did not produce '$resultFile'."
    }

    try {
        [xml]$testResults = Get-Content -LiteralPath $resultFile -Raw
        $testRun = $testResults.'test-run'
    }
    catch {
        throw "Unity $platformName tests produced an invalid result file '$resultFile': $($_.Exception.Message)"
    }

    if ($null -eq $testRun) {
        throw "Unity $platformName tests produced a result file without a test run."
    }

    # As in the backend gate, a skipped or inconclusive test did not run and fails the suite,
    # and so does a suite without tests.
    $notPassed = @($testRun.SelectNodes(".//test-case[@result!='Passed']") |
        ForEach-Object { "$($_.fullname): $($_.result)" })
    if ($notPassed.Count -ne 0) {
        throw "Unity $platformName tests did not run or pass:`n$($notPassed -join "`n")"
    }

    if ($testRun.result -ne "Passed" -or [int]$testRun.total -eq 0 -or
        [int]$testRun.passed -ne [int]$testRun.total) {
        throw "Unity $platformName test results did not report a complete passing suite."
    }

    $summary = "Unity $platformName tests passed: $($testRun.passed)/$($testRun.total)."
    Write-Output $summary
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, "- $summary`n")
    }
}

$runner = if ($useUnityCli -and $Platform -notcontains "StandaloneWindows64") { "the licensed Unity CLI" } else { "the Unity Editor" }
Write-Output "Unity $($Platform -join ' and ') tests passed with Unity $requiredVersion through $runner."

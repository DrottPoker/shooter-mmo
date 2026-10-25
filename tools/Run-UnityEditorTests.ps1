# Runs the Unity EditMode and PlayMode tests in the running Unity Editor that has this checkout's
# Unity project open, through the Unity CLI and the Pipeline package, and fails unless every
# selected test passed. It leaves Play Mode, refreshes assets, waits for compilation and stops at
# once on compile errors, starts each run asynchronously, and polls its status until the run
# completes or times out, so a run never waits without a report. tools/Run-UnityTests.ps1 runs the
# full suites headless when no Editor has the project open.
#
# Exit codes: 0 every selected test passed, 1 a test failed or did not run, 2 the scripts do not
# compile, 3 no Editor has the project open, 4 the Editor did not respond or a run timed out,
# 5 a filter selected no test.
param(
    [ValidateSet("All", "EditMode", "PlayMode")]
    [string]$Mode = "All",
    # Case-insensitive substrings of test full names, such as a fixture name. Each filter runs as
    # its own run in every selected mode. Commas separate filters also when the script runs
    # through powershell -File, which passes an array as one string.
    [string[]]$Filter = @(),
    # What the filters match: part of the test full name, an exact category, or part of the
    # assembly name.
    [ValidateSet("TestName", "Category", "Assembly")]
    [string]$FilterType = "TestName",
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\src\Client"),
    [string]$UnityCliPath = $env:UNITY_CLI_PATH,
    # The time each step may take: becoming idle, compiling, or one test run.
    [int]$TimeoutMinutes = 15,
    [int]$PollSeconds = 2
)

$ErrorActionPreference = "Stop"

$projectPath = [IO.Path]::GetFullPath($ProjectPath).TrimEnd('\', '/')
$filters = @($Filter | ForEach-Object { "$_".Split(",") } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$modes = @(if ($Mode -eq "All") { "EditMode", "PlayMode" } else { $Mode })

if ([string]::IsNullOrWhiteSpace($UnityCliPath)) {
    $command = Get-Command unity -ErrorAction SilentlyContinue
    # Unity Hub installs the CLI below LOCALAPPDATA, which only Windows sets.
    $UnityCliPath = if ($command) { $command.Source }
    elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Unity\bin\unity.exe" }
    else { "" }
}
# Without a CLI no Editor can be reached, so the exit code lets callers run the suites headless.
if ([string]::IsNullOrWhiteSpace($UnityCliPath) -or -not (Test-Path -LiteralPath $UnityCliPath)) {
    Write-Host "Result: the Unity CLI was not found; set UNITY_CLI_PATH, or run tools/Run-UnityTests.ps1 headless."
    exit 3
}

function Stop-Run([int]$ExitCode, [string]$Message) {
    Write-Host "Result: $Message"
    exit $ExitCode
}

# Runs the Unity CLI and returns its exit code, its stdout, and the stdout parsed as JSON when it
# is JSON. Windows PowerShell 5.1 turns redirected native stderr into errors, so errors stay
# suspended while the CLI runs and the exit code decides.
function Invoke-UnityCli([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $records = @(& $UnityCliPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    $stdout = @($records | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $stderr = @($records | Where-Object { $_ -is [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $json = $null
    if ($exitCode -eq 0 -and $stdout.TrimStart().StartsWith("{")) {
        try { $json = $stdout | ConvertFrom-Json } catch { $json = $null }
    }
    [pscustomobject]@{ ExitCode = $exitCode; Text = (@($stdout, $stderr) | Where-Object { $_ }) -join "`n"; Json = $json }
}

function Invoke-EditorCommand([string]$Name, [string[]]$Arguments = @()) {
    Invoke-UnityCli (@("command", $Name) + $Arguments + @("--project-path", $projectPath, "--result-only", "--no-banner"))
}

function Get-Deadline {
    [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
}

function Test-EditorListed {
    $status = Invoke-UnityCli @("status")
    foreach ($line in "$($status.Text)".Split("`n")) {
        $columns = $line.Split("`t")
        if ($columns.Count -ge 3 -and [string]::Equals($columns[2].Trim().TrimEnd('\', '/'), $projectPath, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    $false
}

# Waits until the Editor answers, is not compiling or reloading, and is out of Play Mode. A
# request during a domain reload fails, so failures count as busy until the deadline. An Editor in
# Play Mode reports the status "playing", not "ready", so the play mode alone decides when to stop
# it, and a stop request that a reload dropped is sent again.
function Wait-EditorIdle([string]$Activity) {
    $deadline = Get-Deadline
    $last = $null
    $nextStop = [DateTime]::MinValue
    while ($true) {
        $status = Invoke-EditorCommand "editor_status"
        $state = $status.Json
        if ($state -and -not $state.compiling -and -not $state.domainReloadInProgress) {
            if ($state.status -eq "ready" -and $state.playMode -eq "stopped") {
                return
            }
            if ($state.playMode -ne "stopped" -and [DateTime]::UtcNow -ge $nextStop) {
                Write-Host "Leaving Play Mode."
                $null = Invoke-EditorCommand "editor_stop"
                $nextStop = [DateTime]::UtcNow.AddSeconds(10)
            }
        }
        $last = if ($state) { "status $($state.status), compiling $($state.compiling), play mode $($state.playMode)" } else { "$($status.Text)".Trim() }
        if ([DateTime]::UtcNow -ge $deadline) {
            Stop-Run 4 "the Editor did not become idle $Activity within $TimeoutMinutes minutes; last state: $last. A modal dialog in the Editor blocks it."
        }
        Start-Sleep -Seconds $PollSeconds
    }
}

# Imports changed files and compiles the scripts. An Editor in the background does not refresh by
# itself, and the request may drop when the refresh reloads the domain, so idle state decides.
function Update-EditorScripts {
    $null = Invoke-EditorCommand "eval" @("--code", "UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport); return 0;")
    Wait-EditorIdle "after the asset refresh"
    $check = Invoke-EditorCommand "eval" @("--code", "return UnityEditor.EditorUtility.scriptCompilationFailed;")
    if (-not $check.Json -or -not $check.Json.success) {
        Stop-Run 4 "the Editor could not report its compile state: $("$($check.Text)".Trim())"
    }
    if ($check.Json.result -eq $true) {
        $console = Invoke-EditorCommand "console" @("--level", "error", "--tail", "20")
        $messages = @($console.Json.entries | Where-Object { $_ } | ForEach-Object {
                if ($_.message) { $_.message } elseif ($_.Message) { $_.Message } else { "$_" }
            })
        Write-Host "Compile errors:"
        $messages | Select-Object -Unique | ForEach-Object { Write-Host "  $_" }
        Stop-Run 2 "the scripts do not compile; fix the errors above, then run the tests again"
    }
}

function Write-NotPassed($results) {
    foreach ($result in @($results | Where-Object { $_ -and $_.Status -ne "Passed" })) {
        Write-Host "  $($result.Status): $($result.FullName)"
        foreach ($line in @("$($result.Message)".Trim().Split("`n") | Select-Object -First 8)) {
            if ($line.Trim()) { Write-Host "      $($line.TrimEnd())" }
        }
        foreach ($line in @("$($result.StackTrace)".Trim().Split("`n") | Where-Object { $_.Trim() } | Select-Object -First 3)) {
            Write-Host "      $($line.Trim())"
        }
    }
}

# Starts one asynchronous run and returns its completed status.
function Invoke-TestRun([string]$RunMode, [string]$RunFilter, [string]$Label) {
    $arguments = @("--mode", $(if ($RunMode -eq "EditMode") { "editor" } else { "playmode" }), "--async_tests")
    if ($RunFilter) {
        $arguments += @("--filter", $RunFilter, "--filter_type", $FilterType.ToLowerInvariant())
    }
    $start = Invoke-EditorCommand "run_tests" $arguments
    if (-not $start.Json -or -not $start.Json.success -or $start.Json.result -ne "running") {
        # The request may still have reached the Editor; the run state decides.
        $probe = Invoke-EditorCommand "test_status"
        if (-not $probe.Json -or $probe.Json.status -ne "running") {
            Stop-Run 4 "the Editor did not start the $label`: $("$($start.Text)".Trim())"
        }
    }

    # A filtered run often finishes within a second, so the first poll comes early.
    $deadline = Get-Deadline
    $wait = [Math]::Min(500, $PollSeconds * 1000)
    while ($true) {
        Start-Sleep -Milliseconds $wait
        $wait = $PollSeconds * 1000
        $poll = Invoke-EditorCommand "test_status"
        $status = if ($poll.Json) { "$($poll.Json.status)" } else { "unanswered" }
        switch ($status) {
            "completed" { return [pscustomobject]@{ Label = $label; Filter = $RunFilter; Run = $poll.Json } }
            "error" { Stop-Run 1 "the $label stopped with an error: $($poll.Json.message)" }
            "cancelled" { Stop-Run 1 "the $label were cancelled in the Editor" }
            "no_tests" { Stop-Run 4 "the Editor lost the $label, for example through a restart" }
        }
        # Running, a read inside a status write, or no answer during a Play Mode domain reload.
        if ([DateTime]::UtcNow -ge $deadline) {
            $null = Invoke-EditorCommand "cancel_tests"
            Stop-Run 4 "the $label did not finish within $TimeoutMinutes minutes and were cancelled; last status: $status"
        }
    }
}

if (-not (Test-EditorListed)) {
    Stop-Run 3 "no running Unity Editor has '$projectPath' open. Open it with 'unity open `"$projectPath`"', or run tools/Run-UnityTests.ps1 headless."
}

$started = [Diagnostics.Stopwatch]::StartNew()
Wait-EditorIdle "before the tests"
Update-EditorScripts

$failed = $false
$emptyFilters = New-Object System.Collections.Generic.List[string]
$filterTotals = @{}
# An empty filter runs the whole suite.
$runFilters = @(if ($filters.Count -gt 0) { $filters } else { "" })
$runCount = 0
foreach ($runMode in $modes) {
    foreach ($runFilter in $runFilters) {
        $runCount++
        $label = if ($runFilter) { "$runMode tests matching $($FilterType.ToLowerInvariant()) '$runFilter'" } else { "$runMode tests" }
        Write-Host "Running $label."
        $outcome = Invoke-TestRun $runMode $runFilter $label
        Wait-EditorIdle "after the $label"
        $summary = $outcome.Run.summary
        $total = [int]$summary.total
        Write-Host ("  {0}/{1} passed, {2} failed, {3} skipped, {4} inconclusive in {5:N1} s" -f
            [int]$summary.passed, $total, [int]$summary.failed, [int]$summary.skipped, [int]$summary.inconclusive, [double]$outcome.Run.duration)
        if ($runFilter) {
            $filterTotals[$runFilter] = [int]$filterTotals[$runFilter] + $total
        }
        elseif ($total -eq 0) {
            Write-Host "  The $runMode suite has no tests."
            $failed = $true
        }
        if ([int]$summary.passed -ne $total) {
            Write-NotPassed $outcome.Run.results
            $failed = $true
        }
    }
}
foreach ($runFilter in $filters) {
    if ([int]$filterTotals[$runFilter] -eq 0) {
        $emptyFilters.Add($runFilter)
    }
}

$elapsed = "{0:N0} s" -f $started.Elapsed.TotalSeconds
if ($runCount -ne $modes.Count * $runFilters.Count) {
    Stop-Run 1 "only $runCount of $($modes.Count * $runFilters.Count) test runs started ($elapsed)"
}
if ($failed) {
    Stop-Run 1 "Unity tests failed or did not run ($elapsed)"
}
if ($emptyFilters.Count -gt 0) {
    Stop-Run 5 "no $($modes -join ' or ') test matches $($FilterType.ToLowerInvariant()) $(($emptyFilters | ForEach-Object { "'$_'" }) -join ', ') ($elapsed)"
}
Stop-Run 0 "every selected Unity $($modes -join ' and ') test passed ($elapsed)"

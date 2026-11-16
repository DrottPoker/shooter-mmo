# Measures the worker's replication path in about a minute, as docs/STACK_STRESS_TESTING.md
# describes under Replication Benchmark. It builds Mmorpg.SimulationWorker.Tests in Release, runs
# its Benchmark category, which builds the worker's replication objects around counting peers
# without UDP, GameService, or a database and plays the layouts spread-500, crowd-200, and
# crowd-500 from fixed seeds, prints one table, and writes
# artifacts/replication/<yyyyMMdd-HHmmss>/result.json.
#
# The byte, packet, and record numbers are deterministic: one source state gives the same ones on
# every run and machine. The script fails when an earlier run of the same commit and the same
# uncommitted changes, or the baseline, has other ones. The timing, allocation, and GC numbers
# depend on the machine and what else runs on it, so they are informative only. Without
# -UpdateBaseline it prints the difference to tools/load/baselines/replication.json.
#
# Exit codes: 0 measured, 1 the deterministic numbers differ from a run of the same source state,
# 2 the benchmark could not run.
param(
    # Copies the result to tools/load/baselines/replication.json.
    [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$StackStepPrefix = "replication"
. (Join-Path $PSScriptRoot "LocalStackCommon.ps1")

$testProject = Join-Path $repositoryRoot "tests\Server\Mmorpg.SimulationWorker.Tests"
$runFolder = Join-Path $repositoryRoot ("artifacts\replication\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$resultPath = Join-Path $runFolder "result.json"
$baselinePath = Join-Path $repositoryRoot "tools\load\baselines\replication.json"
$culture = [Globalization.CultureInfo]::InvariantCulture

function Format-Number($Value, [string]$Format = "0.##") {
    if ($null -eq $Value) {
        return "n/a"
    }

    ([double]$Value).ToString($Format, $culture)
}

# The commit is not enough when the tree has changes: the source state also hashes the
# uncommitted diff and every untracked file, so two runs of the same code compare.
function Get-SourceState {
    $status = (Get-NativeLines "git" @("status", "--porcelain")).Lines
    if ($status.Count -eq 0) {
        return "clean"
    }

    $builder = New-Object Text.StringBuilder
    foreach ($line in (Get-NativeLines "git" @("diff", "HEAD", "--binary")).Lines) {
        $null = $builder.AppendLine($line)
    }

    foreach ($file in ((Get-NativeLines "git" @("ls-files", "--others", "--exclude-standard")).Lines | Sort-Object)) {
        $path = Join-Path $repositoryRoot $file
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $null = $builder.AppendLine("$file " + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
        }
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($builder.ToString()))
    }
    finally {
        $sha.Dispose()
    }

    "changes-" + (($hash | ForEach-Object { $_.ToString("x2") }) -join "").Substring(0, 16)
}

# How busy the machine was before the run, in percent of all cores, from three samples.
function Get-MachineLoad {
    $samples = foreach ($index in 1..3) {
        (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
        Start-Sleep -Milliseconds 500
    }

    ($samples | Measure-Object -Average).Average
}

function Write-Result($Result) {
    $rows = foreach ($layout in $Result.layouts) {
        $numbers = $layout.deterministic
        $timing = $layout.timing
        [pscustomobject]@{
            Layout = $layout.name
            "Players/Mobs" = "$($layout.players)/$($layout.mobs)"
            "Snapshot KiB/s per player" = Format-Number ($numbers.snapshotBytesPerPlayerPerSecond / 1024) "0.0"
            "All KiB/s per player" = Format-Number ($numbers.allBytesPerPlayerPerSecond / 1024) "0.0"
            "Packets/s" = Format-Number $numbers.packetsPerSecond "0"
            "Records per player and snapshot" = Format-Number $numbers.recordsPerPlayerPerSnapshot "0.0"
            "Bytes per record" = Format-Number $numbers.bytesPerRecord "0.00"
            "Deferred" = (Format-Number ($numbers.deferredShare * 100) "0.0") + " %"
            "Near moving update rate avg/p5 Hz" = (Format-Number $numbers.updateRateAverageHz "0.0") + " / " + (Format-Number $numbers.updateRateP5Hz "0.0")
            "Moving update rate within 16 m / beyond 58 m Hz" = (Format-Number $numbers.nearestMovingUpdateRateHz "0.0") + " / " + (Format-Number $numbers.farMovingUpdateRateHz "0.0")
            "All entities update rate avg/p5 Hz" = (Format-Number $numbers.allEntitiesUpdateRateAverageHz "0.0") + " / " + (Format-Number $numbers.allEntitiesUpdateRateP5Hz "0.0")
            "Interest avg/p99 ms" = (Format-Number $timing.interestAverageMs "0.00") + " / " + (Format-Number $timing.interestP99Ms "0.00")
            "Broadcast avg/p99 ms" = (Format-Number $timing.broadcastAverageMs "0.00") + " / " + (Format-Number $timing.broadcastP99Ms "0.00")
            "KiB allocated per tick" = Format-Number ($timing.allocatedBytesPerTick / 1024) "0"
            "Gen0/gen1 per s" = (Format-Number $timing.generation0PerSecond "0.0") + " / " + (Format-Number $timing.generation1PerSecond "0.0")
        }
    }

    $rows | Format-List | Out-String -Width 200 | Write-Host

    # Where the time of the interest refresh and of a snapshot goes, per part of their phases.
    Write-Step "Parts of the interest and snapshot phases, average / exact p99 in ms:"
    $partRows = foreach ($layout in $Result.layouts) {
        $row = [ordered]@{ Layout = $layout.name }
        foreach ($part in $layout.timing.phasePartAverageMs.PSObject.Properties) {
            $row[$part.Name] = (Format-Number $part.Value "0.00") + " / " + (Format-Number $layout.timing.phasePartP99Ms.($part.Name) "0.00")
        }

        [pscustomobject]$row
    }

    $partRows | Format-List | Out-String -Width 200 | Write-Host
}

function Write-BaselineDifference($Result, $Baseline) {
    Write-Step "Difference to the baseline of $($Baseline.date) on $($Baseline.machine.cpu) at $($Baseline.commit) ($($Baseline.sourceState)):"
    foreach ($layout in $Result.layouts) {
        $before = $Baseline.layouts | Where-Object { $_.name -eq $layout.name } | Select-Object -First 1
        if (-not $before) {
            Write-Host "  $($layout.name): not in the baseline."
            continue
        }

        Write-Host "  $($layout.name):"
        foreach ($section in @("deterministic", "timing")) {
            foreach ($property in $layout.$section.PSObject.Properties) {
                if ($property.Value -isnot [ValueType]) {
                    continue
                }

                $old = $before.$section.($property.Name)
                $new = $property.Value
                if ($null -eq $old -or [double]$old -eq [double]$new) {
                    continue
                }

                $change = if ([double]$old -ne 0) {
                    " (" + ((([double]$new - [double]$old) / [double]$old) * 100).ToString("+0.0;-0.0;0.0", $culture) + "%)"
                }
                else {
                    ""
                }

                Write-Host ("    {0} {1}: {2} -> {3}{4}" -f $section, $property.Name, (Format-Number $old "0.####"), (Format-Number $new "0.####"), $change)
            }
        }
    }
}

$exitCode = 2
try {
    New-Item -ItemType Directory -Force -Path $runFolder | Out-Null
    Write-Step "Run folder: $runFolder"
    Write-Step "Building Mmorpg.SimulationWorker.Tests in Release."
    if ((Invoke-Logged "dotnet" @("build", $testProject, "--configuration", "Release") (Join-Path $runFolder "build.log")) -ne 0) {
        Write-LogTail (Join-Path $runFolder "build.log")
        throw "The build failed."
    }

    $commit = (Get-NativeLines "git" @("rev-parse", "HEAD")).Lines | Select-Object -First 1
    $sourceState = Get-SourceState
    $load = Get-MachineLoad
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
    Write-Step ("Commit $commit, source state $sourceState; the machine used {0} % of all cores before the run." -f (Format-Number $load "0"))

    $variables = @{
        MMORPG_REPLICATION_RESULT = $resultPath
        MMORPG_REPLICATION_COMMIT = $commit
        MMORPG_REPLICATION_SOURCE_STATE = $sourceState
        MMORPG_REPLICATION_TREE_DIRTY = $(if ($sourceState -eq "clean") { "false" } else { "true" })
        MMORPG_REPLICATION_CPU = $cpu
        MMORPG_REPLICATION_MACHINE_LOAD = $load.ToString($culture)
    }
    foreach ($name in $variables.Keys) {
        Set-Item "Env:$name" $variables[$name]
    }

    Write-Step "Running the layouts spread-500, crowd-200, and crowd-500."
    try {
        $testExit = Invoke-Logged "dotnet" @("test", $testProject, "--configuration", "Release", "--no-build", "--filter", "Category=Benchmark") (Join-Path $runFolder "test.log")
    }
    finally {
        foreach ($name in $variables.Keys) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    if ($testExit -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        Write-LogTail (Join-Path $runFolder "test.log")
        throw "The benchmark test failed."
    }

    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Write-Result $result
    Write-Step "The byte, packet, and record numbers are deterministic: they must be equal on every run and machine for one source state. Their fingerprint is $($result.deterministicFingerprint). The timing, allocation, and GC numbers are informative only."

    # Every earlier run of the same source state, and the baseline when it was one, must agree.
    $exitCode = 0
    $earlier = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "artifacts\replication") -Directory |
            Where-Object { $_.FullName -ne $runFolder } |
            ForEach-Object { Join-Path $_.FullName "result.json" } |
            Where-Object { Test-Path -LiteralPath $_ })
    if (Test-Path -LiteralPath $baselinePath) {
        $earlier += $baselinePath
    }

    $compared = 0
    foreach ($path in $earlier) {
        $other = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($other.commit -ne $result.commit -or $other.sourceState -ne $result.sourceState) {
            continue
        }

        $compared++
        if ($other.deterministicFingerprint -ne $result.deterministicFingerprint) {
            Write-Step "FAILED: $path ran the same source state and has other deterministic numbers ($($other.deterministicFingerprint)); the benchmark is not deterministic."
            $exitCode = 1
        }
    }

    if ($exitCode -eq 0) {
        Write-Step "The deterministic numbers equal those of $compared earlier runs of the same source state."
    }

    if (Test-Path -LiteralPath $baselinePath) {
        Write-BaselineDifference $result (Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json)
    }
    else {
        Write-Step "No baseline yet; -UpdateBaseline records this run as tools/load/baselines/replication.json."
    }

    if ($UpdateBaseline -and $exitCode -eq 0) {
        Copy-Item -LiteralPath $resultPath -Destination $baselinePath -Force
        Write-Step "Recorded this run as the baseline $baselinePath."
    }
}
catch {
    Write-Step "The replication benchmark could not run: $($_.Exception.Message)"
    $exitCode = 2
}

Write-Step "Result: $resultPath"
exit $exitCode

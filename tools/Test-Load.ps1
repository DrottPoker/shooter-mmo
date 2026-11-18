# Runs one load scenario from tools/load/scenarios from start to finish and judges it against
# the quality boundary in tools/load/quality-boundary.json, as docs/STACK_STRESS_TESTING.md
# describes. It builds GameService, SimulationWorker, and StackStressGenerator in Release, asks
# the generator for the scenario's plan, recreates the disposable Docker stack of
# docker-compose.stack-stress.yml when the mode needs it, starts the services as hidden
# processes with the plan's settings, runs the generator, and prints the worker's worst interval
# with its three costliest tick phases and the verdict with the difference to the checked-in
# baseline of the scenario.
#
# Everything of a run goes into artifacts/load/<yyyyMMdd-HHmmss>-<scenario>/: the generator
# report, the service and generator logs, the worker performance file, the PostgreSQL and Redis
# container logs, and summary.json. It always stops what it started, the processes with Ctrl+C
# so the worker shuts down as usual and then the containers, whose logs it keeps first, also
# when the run fails or the script is interrupted with Ctrl+C. It refuses to start while another
# process uses a port it needs. Once everything stopped, StackStressGenerator judge counts the
# warning, error, and critical lines of the service logs and judges the run again with them.
#
# A scenario with a World variant gets it written into the run folder with
# StackStressGenerator world-variant before the worker starts, and the script says whether the
# variant keeps the checked-in collision revision, so a Unity client can join the run.
#
# Under a network profile of tools/load/network-profiles, from the scenario or -Network, the
# script also starts tools/NetworkImpairmentProxy between the bots and the worker, which
# advertises the proxy's port, stops it last, and keeps its counts in the run folder. The verdict
# applies the profile's allowances, and the baseline is the one of the scenario and the profile.
#
# A scenario with a GameService outage has the generator ask for it during the steady interval:
# the script then stops GameService with Ctrl+C, starts it again with the same settings once the
# outage's seconds passed, logging to game-service-2.log, and writes the times it measured to
# outage-window.json, which the generator and the judge read.
#
# Exit codes: 0 the run passed the quality boundary, 1 it ran and failed the boundary or the
# generator's own checks, 2 it could not run, including a run that could not measure because the
# generator or the network proxy itself fell behind.
param(
    # The scenario name, the file name in tools/load/scenarios without .json.
    [Parameter(Mandatory = $true)]
    [string]$Scenario,
    # Overrides the scenario's bot count; the ramp step shrinks to it when it is larger.
    [int]$Bots = 0,
    # Overrides the scenario's steady interval.
    [int]$DurationSeconds = 0,
    # The network profile of tools/load/network-profiles that the bots' traffic goes through, or
    # none for a direct connection; overrides the scenario's.
    [string]$Network = "",
    # Copies the run's summary.json to tools/load/baselines/<scenario>.json, or
    # <scenario>.<profile>.json under a network profile.
    [switch]$UpdateBaseline,
    # How many processors other programs may use together in the seconds before the run without a
    # warning. A desktop with a browser and chat programs uses one to two.
    [double]$QuietMachineProcessors = 2.5,
    # How much of one processor a single other program may use without a warning: a forgotten
    # search or build uses a whole one.
    [double]$QuietProgramProcessors = 0.75,
    # Prints what the script would start and run, and runs nothing.
    [switch]$Plan,
    # How long GameService and the worker may take to become ready.
    [int]$ReadyTimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$StackStepPrefix = "load"
. (Join-Path $PSScriptRoot "LocalStackCommon.ps1")

$composeFile = Join-Path $repositoryRoot "docker-compose.stack-stress.yml"
$scenarioPath = Join-Path $repositoryRoot "tools\load\scenarios\$Scenario.json"
$baselinePath = $null
$runFolder = Join-Path $repositoryRoot ("artifacts\load\" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-$Scenario")
$gameServiceExe = Join-Path $repositoryRoot "src\Server\GameService\bin\Release\net10.0\Mmorpg.GameService.exe"
$workerExe = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Release\net10.0\Mmorpg.SimulationWorker.exe"
$generatorExe = Join-Path $repositoryRoot "tools\StackStressGenerator\bin\Release\net10.0\Mmorpg.Tools.StackStressGenerator.exe"
$proxyExe = Join-Path $repositoryRoot "tools\NetworkImpairmentProxy\bin\Release\net10.0\Mmorpg.Tools.NetworkImpairmentProxy.exe"
$projects = [ordered]@{
    generator = "tools\StackStressGenerator\Mmorpg.Tools.StackStressGenerator.csproj"
    worker = "src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj"
    gameService = "src\Server\GameService\Mmorpg.GameService.csproj"
    proxy = "tools\NetworkImpairmentProxy\Mmorpg.Tools.NetworkImpairmentProxy.csproj"
}

# Runs a program and returns its exit code, standard output, and standard error.
function Invoke-Captured([string]$FilePath, [string[]]$ArgumentList) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = ($ArgumentList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join " "
    $info.WorkingDirectory = $repositoryRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Error = $errorTask.Result }
}

function ConvertTo-Hashtable($Object) {
    $table = @{}
    if ($null -ne $Object) {
        foreach ($property in $Object.PSObject.Properties) {
            $table[$property.Name] = [string]$property.Value
        }
    }

    $table
}

function Format-Number($Value) {
    if ($null -eq $Value) {
        return "n/a"
    }

    ([double]$Value).ToString("0.##", [Globalization.CultureInfo]::InvariantCulture)
}

# Writes what the script measured of the outage, whole, so the generator never reads half of it.
function Save-OutageWindow([string]$Path, $Window) {
    $temporary = "$Path.tmp"
    [IO.File]::WriteAllText($temporary, ($Window | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Get-UtcStamp {
    [DateTime]::UtcNow.ToString("o", [Globalization.CultureInfo]::InvariantCulture)
}

function Write-PlanSection([string]$Title, $Environment, $Sensitive) {
    Write-Host "  $Title"
    foreach ($property in $Environment.PSObject.Properties) {
        $value = if ($Sensitive -contains $property.Name) { "<local test secret>" } else { $property.Value }
        Write-Host "    $($property.Name) = $value"
    }
}

function Write-Plan($LoadPlan) {
    Write-Step "Scenario $($LoadPlan.scenario): $($LoadPlan.description)"
    Write-Host "  Mode $($LoadPlan.mode), workload $($LoadPlan.workload), shard $($LoadPlan.shardId), worker UDP $($LoadPlan.workerUdpPort)."
    if ($LoadPlan.proxy) {
        Write-Host "  Network profile $($LoadPlan.network): the bots join through the network impairment proxy ($proxyExe) on UDP $($LoadPlan.proxy.udpPort), which forwards to the worker on UDP $($LoadPlan.proxy.forwardUdpPort)."
        Write-Host "    arguments: $($LoadPlan.proxy.arguments -join ' ')"
    }
    else {
        Write-Host "  Network profile none: the bots reach the worker directly."
    }

    if ($LoadPlan.needsContainers) {
        Write-Host "  Recreates the containers of docker-compose.stack-stress.yml on TCP $($LoadPlan.containerTcpPorts -join ', ')."
    }

    if ($LoadPlan.worldVariantPath) {
        Write-Host "  Writes the scenario's World variant to $($LoadPlan.worldVariantPath) before the worker starts."
    }

    if ($LoadPlan.outage) {
        Write-Host "  GameService outage: $($LoadPlan.outage.atSeconds) s into the steady interval the generator writes $($LoadPlan.outage.requestPath); the script then stops GameService with Ctrl+C, starts it again $($LoadPlan.outage.seconds) s after the stop with the settings below, logging to game-service-2.log, and writes the stop, exit, start, and ready times to $($LoadPlan.outage.windowPath). Both services write the UTC time of every log entry, so the judge can tell the outage's own lines."
    }

    if ($LoadPlan.needsGameService) {
        Write-PlanSection "GameService ($gameServiceExe) at $($LoadPlan.gameServiceUrl):" $LoadPlan.gameServiceEnvironment $LoadPlan.sensitive
    }

    Write-PlanSection "SimulationWorker ($workerExe):" $LoadPlan.workerEnvironment $LoadPlan.sensitive
    Write-PlanSection "StackStressGenerator ($generatorExe):" $LoadPlan.generatorEnvironment $LoadPlan.sensitive
    $arguments = @()
    $mask = $false
    foreach ($argument in $LoadPlan.generatorArguments) {
        $arguments += if ($mask) { "<local test secret>" } else { $argument }
        $mask = $LoadPlan.sensitive -contains $argument
    }

    Write-Host "    arguments: $($arguments -join ' ')"
    Write-Host "  Run folder: $runFolder"
}

# Prints the generator's new complete output lines and returns the new read position.
function Write-NewOutput([string]$Path, [long]$Offset) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $Offset
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -le $Offset) {
            return $Offset
        }

        $stream.Position = $Offset
        $buffer = New-Object byte[] ($stream.Length - $Offset)
        $read = $stream.Read($buffer, 0, $buffer.Length)
        $end = [Array]::LastIndexOf($buffer, [byte]10, $read - 1)
        if ($end -lt 0) {
            return $Offset
        }

        $text = [Text.Encoding]::Default.GetString($buffer, 0, $end + 1)
        foreach ($line in ($text -split "`r?`n")) {
            if ($line) {
                Write-Host "  | $line"
            }
        }

        return $Offset + $end + 1
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-PortsFree([int[]]$TcpPorts, [int[]]$UdpPorts) {
    foreach ($port in $TcpPorts) {
        $listener = Get-TcpListener $port
        if ($listener) {
            throw "Process $($listener.OwningProcess) already listens on TCP port $port, which the load run needs. Stop it, then run the scenario again."
        }
    }

    foreach ($port in $UdpPorts) {
        $listener = Get-UdpListener $port
        if ($listener) {
            throw "Process $($listener.OwningProcess) already uses UDP port $port, which the load run needs. Stop the SimulationWorker that runs, then run the scenario again."
        }
    }
}

function Build-Project([string]$Project, [string]$LogPath) {
    if ((Invoke-Logged "dotnet" @("build", (Join-Path $repositoryRoot $Project), "--configuration", "Release", "-nologo", "-v", "q") $LogPath) -ne 0) {
        Write-LogTail $LogPath
        throw "Building $Project failed; the log is '$LogPath'."
    }
}

# Writes a container's log into the run folder before the container goes, with standard error,
# where PostgreSQL logs, in the same file.
function Save-ContainerLog([string]$Service, [string]$LogPath) {
    $command = "docker compose -f ""$composeFile"" logs --no-color --no-log-prefix $Service > ""$LogPath"" 2>&1"
    $process = Start-Process -FilePath "cmd.exe" -ArgumentList "/d", "/c", $command -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Write-Step "The log of the $Service container could not be saved."
    }
}

function Write-Soak($Summary) {
    $soak = $Summary.soak
    if ($null -eq $soak) {
        return
    }

    if ($soak.deathsPerBotPerTenMinutes) {
        $deaths = @($soak.deathsPerBotPerTenMinutes.PSObject.Properties | ForEach-Object { "$($_.Name) $(Format-Number $_.Value)" })
        Write-Step "Deaths per bot and ten minutes of the steady interval: $($deaths -join ', ')."
    }

    if ($soak.churn) {
        Write-Step "Churn: $($soak.churn.leaves) leaves, $($soak.churn.rejoinsBody) back to the body, $($soak.churn.rejoinsSaved) back from the saved place, $($soak.churn.rejoinChecksFailed) failed checks; $($soak.churn.churnSkippedUnsafe) not safe in time, $($soak.churn.churnSkippedBusy) still away, $($soak.churn.churnSkippedLate) too late."
    }

    if ($soak.retention) {
        $retention = $soak.retention
        Write-Step "Retention: item_operations $($retention.operationsAtSample) -> $($retention.operationsAtEnd) rows ($(Format-Number $retention.operationGrowthPercent) %), item_operation_changes $($retention.changesAtSample) -> $($retention.changesAtEnd) rows ($(Format-Number $retention.changeGrowthPercent) %), from $(Format-Number $retention.sampleOffsetSeconds) s into the steady interval to its end; worst lag $(Format-Number $retention.maximumLagSeconds) s."
    }

    foreach ($trend in @($soak.memory)) {
        $fifths = @($trend.workingSetFifthsMiB | ForEach-Object { Format-Number $_ }) -join " / "
        Write-Step "Memory of the $($trend.process): working set $(Format-Number $trend.workingSetSlopeMiBPerHour) MiB/h, fifths $fifths MiB."
    }

    if (@($soak.topTables).Count -gt 0) {
        Write-Step "Tables that grew the most per hour of the steady interval:"
        $rows = @($soak.topTables | ForEach-Object {
            [pscustomobject]@{
                Table = $_.table
                Rows = "$($_.startRows) -> $($_.endRows)"
                KiB = "$(Format-Number ($_.startBytes / 1024)) -> $(Format-Number ($_.endBytes / 1024))"
                "Rows/h" = Format-Number $_.rowsPerHour
                "MiB/h" = Format-Number ($_.bytesPerHour / 1MB)
                "KiB/player-h" = Format-Number ($_.bytesPerPlayerHour / 1024)
            }
        })
        $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    }
}

function Write-Verdict($Summary) {
    $rows = @($Summary.verdict | ForEach-Object {
        [pscustomobject]@{
            Rule = $_.rule
            Limit = "$($_.comparison) $(Format-Number $_.limit) $($_.unit)"
            Measured = Format-Number $_.measured
            Result = if ($_.passed) { "pass" } else { "FAIL" }
            Note = $_.note
        }
    })
    $rows | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
}

# The proxy's counts of a run under a network profile, one row per direction.
function Write-NetworkCounts($Summary) {
    $summaryNetwork = $Summary.network
    if ($null -eq $summaryNetwork) {
        return
    }

    if ($null -eq $summaryNetwork.proxy) {
        Write-Step "Network profile $($summaryNetwork.profile): the proxy's counts are missing: $($summaryNetwork.proxyNote)"
        return
    }

    $proxyCounts = $summaryNetwork.proxy
    Write-Step "Network profile $($summaryNetwork.profile) through the proxy, $($proxyCounts.clientsSeen) clients, at most $($proxyCounts.maximumActiveClients) at once:"
    $rows = @(@(
        @{ Name = "client to server"; Counts = $proxyCounts.clientToServer },
        @{ Name = "server to client"; Counts = $proxyCounts.serverToClient }
    ) | ForEach-Object {
        $counts = $_.Counts
        $dropped = [double]$counts.droppedRandom + [double]$counts.droppedBurst + [double]$counts.droppedOutage
        [pscustomobject]@{
            Direction = $_.Name
            Received = $counts.packetsReceived
            Forwarded = $counts.packetsForwarded
            "Dropped %" = if ([double]$counts.packetsReceived -gt 0) { Format-Number ($dropped * 100 / [double]$counts.packetsReceived) } else { "0" }
            "Random/burst/outage" = "$($counts.droppedRandom)/$($counts.droppedBurst)/$($counts.droppedOutage)"
            Duplicated = $counts.packetsDuplicated
            "Delay p50/p99 ms" = "$(Format-Number $counts.delayMs.p50)/$(Format-Number $counts.delayMs.p99)"
            "Late p99/max ms" = "$(Format-Number $counts.latenessMs.p99)/$(Format-Number $counts.latenessMs.maximum)"
        }
    })
    $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}

# What the outage showed: when GameService was away, what the bots heard and met, and when the
# worker noticed the outage and the return.
function Write-Outage($Summary) {
    $outage = $Summary.outage
    if ($null -eq $outage) {
        return
    }

    if ($outage.windowProblem) {
        Write-Step "GameService outage: $($outage.windowProblem)"
        return
    }

    Write-Step ("GameService outage: stopped {0}, ready again {1}, down {2} s. The worker noticed the outage {3} s after the stop and the return {4} s after GameService was ready{5}." -f $outage.stoppedAt, $outage.readyAt, (Format-Number $outage.measuredSeconds), (Format-Number $outage.workerNoticedOutageSeconds), (Format-Number $outage.workerNoticedReturnSeconds), $(if ($outage.workerLogNote) { "; $($outage.workerLogNote)" } else { "" }))
    Write-Host "  Notices: $($outage.botsWithOneNoticeEach) of $($outage.botsAcrossOutage) bots joined across the outage heard one unavailable and one available notice; $($outage.unavailableNotices) unavailable and $($outage.availableNotices) available in all."
    Write-Host "  Expected inside the window: $($outage.expectedRefusals) refusals with game_service_unavailable, $($outage.expectedInFlightFailures) failures of calls it cut off, $($outage.explainedInventoryRefreshFailures) failed item reads, and $($outage.explainedHttpFailures) failed requests; $($outage.unexplainedFailures) of the same answers outside it."
    if ($outage.expectedAnswerLatency) {
        Write-Host "  Those answers took p50 $(Format-Number $outage.expectedAnswerLatency.p50Ms) ms, p95 $(Format-Number $outage.expectedAnswerLatency.p95Ms) ms, at most $(Format-Number $outage.expectedAnswerLatency.maximumMs) ms; the operations' latencies leave them out."
    }
    Write-Host "  Kills while saving was paused: $($outage.killsWhileSavingPaused), corpses seen $($outage.killCorpsesSeen), missing $($outage.killCorpsesMissing). First durable action $(Format-Number $outage.firstDurableSuccessAfterReadySeconds) s after GameService was ready; $($outage.botsWithDurableSuccessAfterReturn) bots committed one after the return."
}

# The worker's steady interval with the worst tick, its loop and GC numbers, and its three
# costliest tick phases, so a failed tick rule names where the tick went.
function Write-WorkerWorstInterval($Summary) {
    $worst = $Summary.workerWorstInterval
    if (-not $worst) {
        return
    }

    Write-Step ("Worst worker interval from {0}: tick p99 {1} ms, average {2} ms; loop thread busy {3}% of the interval, process CPU {4}% of one core, GC pauses {5} ms per second. Its costliest tick phases:" -f `
            $worst.intervalStart, (Format-Number $worst.tickP99Ms), (Format-Number $worst.tickAverageMs), (Format-Number $worst.loopBusyPercent),
            (Format-Number $worst.cpuSingleCorePercent), (Format-Number $worst.gcPauseMsPerSecond))
    $rows = foreach ($phase in @($worst.slowestPhases)) {
        [pscustomobject]@{
            Phase = $phase.name
            "ms per s" = Format-Number $phase.msPerSecond
            Samples = $phase.samples
            "Average ms" = Format-Number $phase.averageMs
            "p99 ms" = Format-Number $phase.p99Ms
            "Maximum ms" = Format-Number $phase.maximumMs
        }
    }

    if ($rows) {
        $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    }
    else {
        Write-Host "  The worker's file names no tick phases."
    }
}

# The processors that every other process used over some seconds, and the three that used the
# most. A search through the disk that somebody forgot, a build, or a game slows every phase of
# the tick, and the run then says more about the machine than about the code.
function Measure-OtherPrograms([int]$Seconds) {
    $before = @{}
    foreach ($process in Get-Process) {
        if ($null -ne $process.CPU) { $before[$process.Id] = $process.CPU }
    }

    Start-Sleep -Seconds $Seconds
    $used = @(foreach ($process in Get-Process) {
            if ($process.Id -ne $PID -and $before.ContainsKey($process.Id) -and $null -ne $process.CPU) {
                [pscustomobject]@{
                    name = $process.ProcessName
                    processors = [math]::Max(0.0, [double]($process.CPU - $before[$process.Id]) / $Seconds)
                }
            }
        })
    $top = @($used | Sort-Object processors -Descending | Select-Object -First 3 | Where-Object { $_.processors -ge 0.05 })
    [pscustomobject]@{
        seconds = $Seconds
        processors = [math]::Round(($used | Measure-Object processors -Sum).Sum, 2)
        largest = [math]::Round(($used | Measure-Object processors -Maximum).Maximum, 2)
        top = (($top | ForEach-Object { "{0} {1}" -f $_.name, (Format-Number ([math]::Round($_.processors, 1))) }) -join ", ")
    }
}

function Write-BaselineDifference($Summary) {
    if (-not (Test-Path -LiteralPath $baselinePath)) {
        Write-Step "No checked-in baseline for $Scenario under the network profile $runNetwork; -UpdateBaseline records this run as one."
        return
    }

    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    $baselineNetwork = if ($baseline.network) { $baseline.network.profile } else { "none" }
    if ($baselineNetwork -ne $runNetwork) {
        Write-Step "The baseline $baselinePath was measured under the network profile $baselineNetwork, not $runNetwork; no difference is shown."
        return
    }

    Write-Step "Difference to the baseline of $($baseline.date) on $($baseline.machine.cpu) at $($baseline.commit) (informative only):"
    $names = @($baseline.keyNumbers.PSObject.Properties.Name) + @($Summary.keyNumbers.PSObject.Properties.Name) | Sort-Object -Unique
    foreach ($name in $names) {
        $before = $baseline.keyNumbers.$name
        $after = $Summary.keyNumbers.$name
        $change = if ($null -ne $before -and $null -ne $after -and [double]$before -ne 0) {
            " (" + (((([double]$after - [double]$before) / [double]$before) * 100).ToString("+0.0;-0.0;0.0", [Globalization.CultureInfo]::InvariantCulture)) + "%)"
        }
        else {
            ""
        }

        Write-Host ("  {0}: {1} -> {2}{3}" -f $name, (Format-Number $before), (Format-Number $after), $change)
    }
}

if (-not (Test-Path -LiteralPath $scenarioPath)) {
    $known = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tools\load\scenarios") -Filter "*.json" | ForEach-Object { $_.BaseName }
    Write-Step "The scenario '$Scenario' does not exist. Scenarios: $($known -join ', ')."
    exit 2
}

$total = [Diagnostics.Stopwatch]::StartNew()
$exitCode = 2
$machineWarning = $null
$generatorExit = $null
$couldNotMeasure = $false
$gameService = $null
$worker = $null
$generator = $null
$proxy = $null
# The plan names the profile; until it ran, the one -Network asked for.
$runNetwork = if ($Network) { $Network } else { "none" }
$containersStarted = $false
$summaryPath = $null
Push-Location $repositoryRoot
try {
    if (-not $Plan) {
        New-Item -ItemType Directory -Force -Path $runFolder | Out-Null
        Write-Step "Run folder: $runFolder"
    }

    $buildLogRoot = [IO.Path]::GetTempPath()
    Write-Step "Building StackStressGenerator in Release."
    $generatorBuildLog = if ($Plan) { Join-Path $buildLogRoot ("build-generator-" + [Guid]::NewGuid().ToString("N") + ".log") } else { Join-Path $runFolder "build-generator.log" }
    try {
        Build-Project $projects.generator $generatorBuildLog
    }
    finally {
        if ($Plan) {
            Remove-Item -LiteralPath $generatorBuildLog -ErrorAction SilentlyContinue
        }
    }

    $commit = (Get-NativeLines "git" @("rev-parse", "HEAD")).Lines | Select-Object -First 1
    $dirty = (Get-NativeLines "git" @("status", "--porcelain")).Lines.Count -gt 0
    $planArguments = @("plan", "--scenario", $scenarioPath, "--run-folder", $runFolder, "--source-tree-dirty", $(if ($dirty) { "true" } else { "false" }))
    if ($commit) {
        $planArguments += @("--source-commit", $commit)
    }

    if ($Bots -gt 0) {
        $planArguments += @("--bots", "$Bots")
    }

    if ($DurationSeconds -gt 0) {
        $planArguments += @("--duration-seconds", "$DurationSeconds")
    }

    if ($Network) {
        $planArguments += @("--network", $Network)
    }

    $planResult = Invoke-Captured $generatorExe $planArguments
    if ($planResult.ExitCode -ne 0) {
        throw "The scenario is invalid: $($planResult.Error.Trim())"
    }

    $loadPlan = $planResult.Output | ConvertFrom-Json
    # A baseline belongs to its scenario and network profile, so a run under a profile never
    # replaces or compares with the direct one.
    $runNetwork = $loadPlan.network
    $baselineName = if ($runNetwork -eq "none") { "$Scenario.json" } else { "$Scenario.$runNetwork.json" }
    $baselinePath = Join-Path $repositoryRoot "tools\load\baselines\$baselineName"
    Write-Plan $loadPlan
    if ($Plan) {
        $exitCode = 0
    }
    else {
        $summaryPath = $loadPlan.summaryPath
        Assert-PortsFree @($loadPlan.tcpPorts) @($loadPlan.udpPorts)
        if ($loadPlan.worldVariantPath) {
            Write-Step "Writing the scenario's World variant with the content compilers."
            $variantResult = Invoke-Captured $generatorExe @("world-variant", "--scenario", $scenarioPath, "--run-folder", $runFolder)
            if ($variantResult.ExitCode -ne 0) {
                throw "The World variant could not be written: $($variantResult.Error.Trim())"
            }

            foreach ($line in ($variantResult.Output -split "`r?`n")) {
                if ($line) {
                    Write-Host "  $line"
                }
            }
        }

        Write-Step "Building SimulationWorker$(if ($loadPlan.needsGameService) { ' and GameService' })$(if ($loadPlan.proxy) { ' and the network impairment proxy' }) in Release."
        Build-Project $projects.worker (Join-Path $runFolder "build-worker.log")
        if ($loadPlan.needsGameService) {
            Build-Project $projects.gameService (Join-Path $runFolder "build-game-service.log")
        }

        if ($loadPlan.proxy) {
            Build-Project $projects.proxy (Join-Path $runFolder "build-network-proxy.log")
        }

        # After the builds and before anything of the run starts.
        $machineBefore = Measure-OtherPrograms 10
        $machineBefore | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runFolder "machine-before.json") -Encoding utf8
        $machineLine = "Other programs used $(Format-Number $machineBefore.processors) processors in the $($machineBefore.seconds) s before the run$(if ($machineBefore.top) { ": $($machineBefore.top)" })."
        Write-Step $machineLine
        if ($machineBefore.processors -gt $QuietMachineProcessors -or $machineBefore.largest -gt $QuietProgramProcessors) {
            $machineWarning = "WARNING: the machine was not quiet before the run. $machineLine Its numbers are not comparable with a baseline; stop what uses the processors and run it again."
            Write-Step $machineWarning
        }

        if ($loadPlan.needsContainers) {
            if ((Get-NativeLines "docker" @("compose", "-f", $composeFile, "config", "--services")).ExitCode -ne 0) {
                throw "Docker Compose cannot read docker-compose.stack-stress.yml; is Docker running?"
            }

            Write-Step "Recreating the disposable stack."
            $containersStarted = $true
            if ((Invoke-Logged "docker" @("compose", "-f", $composeFile, "down") (Join-Path $runFolder "docker-compose-down-before.log")) -ne 0) {
                Write-LogTail (Join-Path $runFolder "docker-compose-down-before.log")
                throw "docker compose down failed."
            }

            Assert-PortsFree @($loadPlan.containerTcpPorts) @()
            if ((Invoke-Logged "docker" @("compose", "-f", $composeFile, "up", "-d", "--wait") (Join-Path $runFolder "docker-compose-up.log")) -ne 0) {
                Write-LogTail (Join-Path $runFolder "docker-compose-up.log")
                throw "docker compose up failed. A bind error means Windows reserved one of the ports $($loadPlan.containerTcpPorts -join ', '); see docs/STACK_STRESS_TESTING.md."
            }
        }

        $workerLog = Join-Path $runFolder "simulation-worker.log"
        $generatorLog = Join-Path $runFolder "generator.log"
        $udpLine = "Realtime transport listening on UDP port $($loadPlan.workerUdpPort)"
        if ($loadPlan.proxy) {
            # The proxy needs nothing else, and it must listen before the first bot joins.
            Write-Step "Starting the network impairment proxy with the profile $runNetwork."
            $proxyLog = Join-Path $runFolder "network-proxy.log"
            $proxy = Start-StackProcess "network-proxy" $proxyExe @($loadPlan.proxy.arguments) @{} $runFolder $repositoryRoot
            Wait-Until { (Read-SharedText $proxyLog) -match "listening on UDP" } "The network impairment proxy" $proxy $proxyLog $ReadyTimeoutSeconds $runFolder
        }

        if ($loadPlan.needsGameService) {
            Write-Step "Starting GameService."
            $gameService = Start-StackProcess "game-service" $gameServiceExe @() (ConvertTo-Hashtable $loadPlan.gameServiceEnvironment) $runFolder $repositoryRoot
            Wait-Until { Test-HttpReady "$($loadPlan.gameServiceUrl)/health/ready" } "GameService readiness" $gameService (Join-Path $runFolder "game-service.log") $ReadyTimeoutSeconds $runFolder

            Write-Step "Starting SimulationWorker."
            $worker = Start-StackProcess "simulation-worker" $workerExe @() (ConvertTo-Hashtable $loadPlan.workerEnvironment) $runFolder $repositoryRoot
            Wait-Until { (Read-SharedText $workerLog) -match $udpLine } "The worker's UDP listener" $worker $workerLog $ReadyTimeoutSeconds $runFolder
            Wait-Until { Test-ShardOnline $loadPlan.gameServiceUrl $loadPlan.shardId } "The registration of $($loadPlan.shardId)" $worker $workerLog $ReadyTimeoutSeconds $runFolder

            Write-Step "Starting StackStressGenerator."
            $generator = Start-StackProcess "generator" $generatorExe @($loadPlan.generatorArguments) (ConvertTo-Hashtable $loadPlan.generatorEnvironment) $runFolder $repositoryRoot
            # Windows PowerShell reports an exit code only for a process whose handle was opened.
            $null = $generator.Handle
        }
        else {
            # In worker-only mode the generator is the worker's authority, so it starts first.
            Write-Step "Starting StackStressGenerator, the worker's in-memory authority."
            $generator = Start-StackProcess "generator" $generatorExe @($loadPlan.generatorArguments) (ConvertTo-Hashtable $loadPlan.generatorEnvironment) $runFolder $repositoryRoot
            # Windows PowerShell reports an exit code only for a process whose handle was opened.
            $null = $generator.Handle
            $authorityPort = ([Uri]$loadPlan.authorityUrl).Port
            Wait-Until { $null -ne (Get-TcpListener $authorityPort) } "The generator's authority" $generator $generatorLog $ReadyTimeoutSeconds $runFolder

            Write-Step "Starting SimulationWorker."
            $worker = Start-StackProcess "simulation-worker" $workerExe @() (ConvertTo-Hashtable $loadPlan.workerEnvironment) $runFolder $repositoryRoot
            Wait-Until { (Read-SharedText $workerLog) -match $udpLine } "The worker's UDP listener" $worker $workerLog $ReadyTimeoutSeconds $runFolder
        }

        $offset = 0L
        $workerStopped = $false
        $gameServiceLog = Join-Path $runFolder "game-service.log"
        # The GameService outage moves through waiting, stopped, starting, and ready.
        $outage = $loadPlan.outage
        $outageState = "waiting"
        $outageWindow = [ordered]@{ requestedAt = $null; stoppedAt = $null; exitedAt = $null; restartedAt = $null; readyAt = $null }
        $outageStopped = $null
        $outageRestarted = $null
        while (-not $generator.HasExited) {
            $offset = Write-NewOutput $generatorLog $offset
            if ($outage -and $outageState -eq "waiting" -and (Test-Path -LiteralPath $outage.requestPath)) {
                $outageWindow.requestedAt = (Get-Content -LiteralPath $outage.requestPath -Raw | ConvertFrom-Json).requestedAt
                Write-Step "The generator asked for the GameService outage: stopping GameService for $($outage.seconds) s."
                $outageStopped = [Diagnostics.Stopwatch]::StartNew()
                $outageWindow.stoppedAt = Get-UtcStamp
                Save-OutageWindow $outage.windowPath $outageWindow
                Stop-StackProcess $gameService "GameService"
                $gameService = $null
                $outageWindow.exitedAt = Get-UtcStamp
                Save-OutageWindow $outage.windowPath $outageWindow
                $outageState = "stopped"
            }
            elseif ($outage -and $outageState -eq "stopped" -and $outageStopped.Elapsed.TotalSeconds -ge $outage.seconds) {
                Write-Step "Starting GameService again $($outage.seconds) s after the stop, with the same settings."
                $gameServiceLog = Join-Path $runFolder "game-service-2.log"
                $gameService = Start-StackProcess "game-service-2" $gameServiceExe @() (ConvertTo-Hashtable $loadPlan.gameServiceEnvironment) $runFolder $repositoryRoot
                $outageRestarted = [Diagnostics.Stopwatch]::StartNew()
                $outageWindow.restartedAt = Get-UtcStamp
                Save-OutageWindow $outage.windowPath $outageWindow
                $outageState = "starting"
            }
            elseif ($outage -and $outageState -eq "starting") {
                if (Test-HttpReady "$($loadPlan.gameServiceUrl)/health/ready") {
                    $outageWindow.readyAt = Get-UtcStamp
                    Save-OutageWindow $outage.windowPath $outageWindow
                    Write-Step ("GameService is ready again {0:N1} s after the stop." -f $outageStopped.Elapsed.TotalSeconds)
                    $outageState = "ready"
                }
                elseif ($outageRestarted.Elapsed.TotalSeconds -gt $ReadyTimeoutSeconds) {
                    throw "GameService did not become ready again within $ReadyTimeoutSeconds s of its restart; its log is game-service-2.log in '$runFolder'."
                }
            }

            if (-not $loadPlan.needsGameService -and -not $workerStopped -and (Test-Path -LiteralPath $summaryPath)) {
                # In worker-only mode the generator keeps its authority up after the summary, so the
                # worker stops against it; then the generator stops with its own exit code.
                Stop-StackProcess $worker "SimulationWorker"
                $workerStopped = $true
                Stop-StackProcess $generator "StackStressGenerator"
                continue
            }

            if ($worker.HasExited -and -not $workerStopped) {
                Write-LogTail $workerLog
                throw "SimulationWorker ended during the run; its log is in '$runFolder'."
            }

            if ($null -ne $gameService -and $gameService.HasExited) {
                Write-LogTail $gameServiceLog
                throw "GameService ended during the run; its log is in '$runFolder'."
            }

            if ($null -ne $proxy -and $proxy.HasExited) {
                Write-LogTail (Join-Path $runFolder "network-proxy.err.log")
                throw "The network impairment proxy ended during the run; its log is in '$runFolder'."
            }

            Start-Sleep -Milliseconds 500
        }

        $generator.WaitForExit()
        $null = Write-NewOutput $generatorLog $offset
        Write-LogTail (Join-Path $runFolder "generator.err.log")
        $generatorExit = $generator.ExitCode
        if (-not ((Test-Path -LiteralPath $summaryPath) -and $generatorExit -in @(0, 1, 3, 4))) {
            throw "The generator ended with exit code $generatorExit before it wrote a summary; its log is in '$runFolder'."
        }
    }
}
catch {
    Write-Step "The load run could not run: $($_.Exception.Message)"
    $exitCode = 2
}
finally {
    Stop-StackProcess $generator "StackStressGenerator"
    Stop-StackProcess $worker "SimulationWorker"
    Stop-StackProcess $gameService "GameService"
    # The proxy stops last, so the worker's last packets still pass, and writes its counts.
    Stop-StackProcess $proxy "NetworkImpairmentProxy"
    if ($containersStarted) {
        # The containers' logs go with them, so they are kept first: a deadlock's other statement
        # is only in the PostgreSQL log.
        Save-ContainerLog "postgres" (Join-Path $runFolder "postgres.log")
        Save-ContainerLog "redis" (Join-Path $runFolder "redis.log")
        $null = Invoke-Logged "docker" @("compose", "-f", $composeFile, "down") (Join-Path $runFolder "docker-compose-down.log")
        Write-Step "Kept the PostgreSQL and Redis logs and removed the disposable stack."
    }

    Pop-Location
}

if ($Plan) {
    exit $exitCode
}

if ($null -ne $generatorExit -and $generatorExit -in @(0, 1, 3, 4) -and (Test-Path -LiteralPath $summaryPath)) {
    # Every process stopped, so the service logs are complete: they are judged now, and the
    # judgement decides the result.
    Write-Step "Judging the service logs."
    $judge = Invoke-Captured $generatorExe @("judge", "--run-folder", $runFolder, "--quality-boundary", (Join-Path $repositoryRoot "tools\load\quality-boundary.json"))
    foreach ($line in (($judge.Output + $judge.Error) -split "`r?`n")) {
        if ($line) {
            Write-Host "  | $line"
        }
    }

    switch ($judge.ExitCode) {
        0 { $exitCode = 0 }
        1 { $exitCode = 1 }
        4 {
            # The generator or the proxy fell behind, so the run measured nothing: it could not run.
            $couldNotMeasure = $true
            $exitCode = 2
        }
        default {
            Write-Step "The run could not be judged."
            $exitCode = 2
        }
    }
}

if ($summaryPath -and (Test-Path -LiteralPath $summaryPath)) {
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    Write-Soak $summary
    Write-NetworkCounts $summary
    Write-Outage $summary
    Write-WorkerWorstInterval $summary
    Write-Step "Quality boundary of $Scenario under the network profile $runNetwork ($($summary.bots) bots, $($summary.steadySeconds) s steady):"
    Write-Verdict $summary
    Write-BaselineDifference $summary
    if ($UpdateBaseline) {
        Copy-Item -LiteralPath $summaryPath -Destination $baselinePath -Force
        Write-Step "Recorded this run as the baseline $baselinePath."
    }
}

$result = if ($exitCode -eq 0) { "passed" } elseif ($exitCode -eq 1) { "FAILED" } elseif ($couldNotMeasure) { "could not measure: the generator or the network proxy fell behind, see the validity rules" } else { "could not run" }
if ($machineWarning) {
    Write-Step $machineWarning
}

Write-Step ("Load scenario $Scenario under the network profile $runNetwork $result in {0:N0} s. Run folder: $runFolder" -f $total.Elapsed.TotalSeconds)
exit $exitCode

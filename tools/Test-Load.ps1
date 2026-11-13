# Runs one load scenario from tools/load/scenarios from start to finish and judges it against
# the quality boundary in tools/load/quality-boundary.json, as docs/STACK_STRESS_TESTING.md
# describes. It builds GameService, SimulationWorker, and StackStressGenerator in Release, asks
# the generator for the scenario's plan, recreates the disposable Docker stack of
# docker-compose.stack-stress.yml when the mode needs it, starts the services as hidden
# processes with the plan's settings, runs the generator, and prints the verdict with the
# difference to the checked-in baseline of the scenario.
#
# Everything of a run goes into artifacts/load/<yyyyMMdd-HHmmss>-<scenario>/: the generator
# report, the service and generator logs, the worker performance file, and summary.json. It
# always stops what it started, the processes with Ctrl+C so the worker shuts down as usual and
# then the containers, also when the run fails or the script is interrupted with Ctrl+C. It
# refuses to start while another process uses a port it needs.
#
# A scenario with a World variant gets it written into the run folder with
# StackStressGenerator world-variant before the worker starts, and the script says whether the
# variant keeps the checked-in collision revision, so a Unity client can join the run.
#
# Exit codes: 0 the run passed the quality boundary, 1 it ran and failed the boundary or the
# generator's own checks, 2 it could not run, including a run that could not measure because the
# generator itself fell behind.
param(
    # The scenario name, the file name in tools/load/scenarios without .json.
    [Parameter(Mandatory = $true)]
    [string]$Scenario,
    # Overrides the scenario's bot count; the ramp step shrinks to it when it is larger.
    [int]$Bots = 0,
    # Overrides the scenario's steady interval.
    [int]$DurationSeconds = 0,
    # Copies the run's summary.json to tools/load/baselines/<scenario>.json.
    [switch]$UpdateBaseline,
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
$baselinePath = Join-Path $repositoryRoot "tools\load\baselines\$Scenario.json"
$runFolder = Join-Path $repositoryRoot ("artifacts\load\" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-$Scenario")
$gameServiceExe = Join-Path $repositoryRoot "src\Server\GameService\bin\Release\net10.0\Mmorpg.GameService.exe"
$workerExe = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Release\net10.0\Mmorpg.SimulationWorker.exe"
$generatorExe = Join-Path $repositoryRoot "tools\StackStressGenerator\bin\Release\net10.0\Mmorpg.Tools.StackStressGenerator.exe"
$projects = [ordered]@{
    generator = "tools\StackStressGenerator\Mmorpg.Tools.StackStressGenerator.csproj"
    worker = "src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj"
    gameService = "src\Server\GameService\Mmorpg.GameService.csproj"
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
    if ($LoadPlan.needsContainers) {
        Write-Host "  Recreates the containers of docker-compose.stack-stress.yml on TCP $($LoadPlan.containerTcpPorts -join ', ')."
    }

    if ($LoadPlan.worldVariantPath) {
        Write-Host "  Writes the scenario's World variant to $($LoadPlan.worldVariantPath) before the worker starts."
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

function Write-BaselineDifference($Summary) {
    if (-not (Test-Path -LiteralPath $baselinePath)) {
        Write-Step "No checked-in baseline for $Scenario; -UpdateBaseline records this run as one."
        return
    }

    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
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
$couldNotMeasure = $false
$gameService = $null
$worker = $null
$generator = $null
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

    $planResult = Invoke-Captured $generatorExe $planArguments
    if ($planResult.ExitCode -ne 0) {
        throw "The scenario is invalid: $($planResult.Error.Trim())"
    }

    $loadPlan = $planResult.Output | ConvertFrom-Json
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

        Write-Step "Building SimulationWorker$(if ($loadPlan.needsGameService) { ' and GameService' }) in Release."
        Build-Project $projects.worker (Join-Path $runFolder "build-worker.log")
        if ($loadPlan.needsGameService) {
            Build-Project $projects.gameService (Join-Path $runFolder "build-game-service.log")
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
        while (-not $generator.HasExited) {
            $offset = Write-NewOutput $generatorLog $offset
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
                Write-LogTail (Join-Path $runFolder "game-service.log")
                throw "GameService ended during the run; its log is in '$runFolder'."
            }

            Start-Sleep -Milliseconds 500
        }

        $generator.WaitForExit()
        $null = Write-NewOutput $generatorLog $offset
        Write-LogTail (Join-Path $runFolder "generator.err.log")
        $generatorExit = $generator.ExitCode
        if ((Test-Path -LiteralPath $summaryPath) -and $generatorExit -in @(0, 1, 3)) {
            $exitCode = if ($generatorExit -eq 0) { 0 } else { 1 }
        }
        elseif ((Test-Path -LiteralPath $summaryPath) -and $generatorExit -eq 4) {
            # The generator fell behind, so the run measured nothing: it could not run.
            $couldNotMeasure = $true
            $exitCode = 2
        }
        else {
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
    if ($containersStarted) {
        $null = Invoke-Logged "docker" @("compose", "-f", $composeFile, "down") (Join-Path $runFolder "docker-compose-down.log")
        Write-Step "Removed the disposable stack."
    }

    Pop-Location
}

if ($Plan) {
    exit $exitCode
}

if ($summaryPath -and (Test-Path -LiteralPath $summaryPath)) {
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    Write-Step "Quality boundary of $Scenario ($($summary.bots) bots, $($summary.steadySeconds) s steady):"
    Write-Verdict $summary
    Write-BaselineDifference $summary
    if ($UpdateBaseline) {
        Copy-Item -LiteralPath $summaryPath -Destination $baselinePath -Force
        Write-Step "Recorded this run as the baseline $baselinePath."
    }
}

$result = if ($exitCode -eq 0) { "passed" } elseif ($exitCode -eq 1) { "FAILED" } elseif ($couldNotMeasure) { "could not measure: the generator fell behind" } else { "could not run" }
Write-Step ("Load scenario $Scenario $result in {0:N0} s. Run folder: $runFolder" -f $total.Elapsed.TotalSeconds)
exit $exitCode

# Measures how the Unity client copes with a crowd: the crowd baseline of the client. It starts
# a local stack with the stress arena, local-shard-2 with Development World 1, where every player
# stays inside one interest radius, and for each size of -Bots it starts that many
# ActiveSimulationBots, waits until all have joined, and runs the ClientCrowd PlayMode test,
# which enters the arena as a player, waits until the whole crowd is shown, and records the
# client measurements for -Seconds. It stops the bots and waits until their bodies are gone
# before the next size.
#
# -Platform Player runs the test in a Windows IL2CPP test player that compiles like a release
# build, in a 1920 by 1080 window on the desktop, as tools/Run-UnityTests.ps1 builds it. Before
# each run it waits until other programs, such as another Unity Editor, a build, or a game, have
# used at most two and a half processors and 10 % of the graphics card's 3D engine for thirty
# seconds, samples them every few seconds during the run, and repeats a run whose recorded
# seconds they disturbed, whose test failed, or whose samples do not cover the recorded seconds,
# so no result stands without proof that nothing else used the machine. -Platform Editor starts
# the stack and the bots of the first size and keeps them for -HoldMinutes, for a person who runs
# the test in an open Editor, whose Play Mode needs its window for honest frame times; the
# headless Editor draws nothing.
#
# GameService gets the development bot endpoint with a secret that the script makes for this run
# and passes to GameService and the bots only, so the .env file is not read or changed. The script
# stops everything it started, also when a run fails or it is interrupted with Ctrl+C, and refuses
# to run while GameService or the stress arena's ports are in use or, with -Platform Player, a
# Unity Editor has the project open. The logs and the result of every run stay in a folder below
# the system temp directory, which the script prints.
#
# Exit codes: 0 every size was measured, 1 a measurement failed, 2 the stack or Unity could not
# run it.
param(
    [ValidateSet("Player", "Editor")]
    [string]$Platform = "Player",
    # The crowd sizes, measured in this order.
    [int[]]$Bots = @(100, 200, 400),
    # How long each recording lasts after the whole crowd is shown.
    [int]$Seconds = 60,
    # How the test player compiles; the baseline uses a release build.
    [ValidateSet("Release", "Development")]
    [string]$PlayerBuild = "Release",
    # How often a disturbed run is repeated.
    [int]$Attempts = 3,
    # With -Platform Editor: how long the stack and the bots stay up.
    [int]$HoldMinutes = 30,
    # How long a measurement waits for other programs to leave the machine alone.
    [int]$QuietTimeoutMinutes = 30,
    # Other programs that use more than this many processors, or more than this part of the
    # graphics card's 3D engine, on average disturb a measurement. Two and a half processors are
    # the background of an ordinary desktop.
    [double]$DisturbedProcessors = 2.5,
    [double]$DisturbedGpuPercent = 10.0,
    # The longest time within the recorded seconds that may pass without a sample of what other
    # programs use. A sample comes about every three seconds, up to seven on a busy machine.
    [double]$LoadSampleGapSeconds = 10.0,
    # How long GameService, the worker, and the bots may take to become ready.
    [int]$ReadyTimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repositoryRoot "src\Client"
$runUnityTests = Join-Path $PSScriptRoot "Run-UnityTests.ps1"
$logRoot = Join-Path ([IO.Path]::GetTempPath()) ("shooter-mmo-client-crowd\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$gameServiceUrl = "http://localhost:5000"
$gameServicePort = 5000
$shardId = "local-shard-2"
$udpPort = 27016
$category = "ClientCrowd"
# Release builds, so the services and the bots, which share the machine with the client, take
# as little of it as they can.
$gameServiceDll = Join-Path $repositoryRoot "src\Server\GameService\bin\Release\net10.0\Mmorpg.GameService.dll"
$workerDll = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Release\net10.0\Mmorpg.SimulationWorker.dll"
$botsDll = Join-Path $repositoryRoot "tools\ActiveSimulationBots\bin\Release\net10.0\Mmorpg.Tools.ActiveSimulationBots.dll"
# The variables the crowd test reads.
$crowdVariables = @("MMORPG_CROWD_BOTS", "MMORPG_CROWD_SECONDS", "MMORPG_CROWD_RESULTS", "MMORPG_CROWD_LABEL")

$StackStepPrefix = "crowd"
. (Join-Path $PSScriptRoot "LocalStackCommon.ps1")
. (Join-Path $PSScriptRoot "UnityCommon.ps1")

# The ids of the processes that are part of the measurement by where they come from: the
# processes that the script with the process id $RootProcessId started, directly or through
# another one (the services, the bots, the Unity Editor that builds and starts the test player,
# the player, and the load monitor), a test player below the project, and this project's Unity
# Editor with its import workers. Command lines are only compared, never printed: an Editor's
# holds an access token.
function Get-MeasurementProcessIds([string]$OwnProjectPath, [int]$RootProcessId) {
    $ownPath = $OwnProjectPath.Replace('/', '\').TrimEnd('\')
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name, ExecutablePath)
    $byId = @{}
    foreach ($process in $all) {
        $byId[[int]$process.ProcessId] = $process
    }

    $children = @{}
    foreach ($process in $all) {
        # A parent that started after its child is another process that got the number of the
        # real parent, which has ended.
        $parent = $byId[[int]$process.ParentProcessId]
        if ($null -ne $parent -and $null -ne $parent.CreationDate -and $null -ne $process.CreationDate -and
            $parent.CreationDate -le $process.CreationDate -and $parent.ProcessId -ne $process.ProcessId) {
            $children[[int]$parent.ProcessId] = @($children[[int]$parent.ProcessId]) + [int]$process.ProcessId
        }
    }

    $owned = @{}
    $queue = [Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootProcessId)
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if ($owned.ContainsKey($id)) {
            continue
        }

        $owned[$id] = $true
        foreach ($child in @($children[$id])) {
            if ($null -ne $child) {
                $queue.Enqueue($child)
            }
        }
    }

    foreach ($process in $all) {
        if ($process.Name -eq "PlayerWithTests.exe" -and
            ([string]$process.ExecutablePath).StartsWith($ownPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $owned[[int]$process.ProcessId] = $true
        }
    }

    # The Editor names the project with backslashes, its asset import workers with slashes.
    foreach ($unity in @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'")) {
        if (([string]$unity.CommandLine).Replace('/', '\').IndexOf($ownPath, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $owned[[int]$unity.ProcessId] = $true
        }
    }

    $owned
}

# What one sample of the machine's load says about everything that is not the measurement: the
# processor seconds of every other process since the last sample, and the 3D engine use of the
# graphics card by every other process, with the busiest of each. Part of the measurement are
# the processes of Get-MeasurementProcessIds and, by name, Docker with the containers, the
# desktop compositor that shows the player's window, and the system itself. Everything else is
# not, also when it has the name of a process of the measurement: another .NET program or
# PowerShell script, another Unity Editor, such as the CI runner's, a build, or a game.
function Get-ForeignLoadSample([string]$OwnProjectPath, [hashtable]$Previous, [int]$RootProcessId) {
    $measurement = @("vmmem", "VmmemWSL", "com.docker.backend", "com.docker.build", "Docker Desktop",
        "dwm", "System", "Idle", "Registry", "Memory Compression", "csrss", "smss", "wininit",
        "services", "lsass", "WmiPrvSE", "audiodg")
    $owned = Get-MeasurementProcessIds $OwnProjectPath $RootProcessId
    $seconds = 0.0
    $byName = @{}
    $names = @{}
    foreach ($process in Get-Process) {
        $names[$process.Id] = $process.ProcessName
        if ($null -eq $process.TotalProcessorTime) {
            continue
        }

        $cpu = $process.TotalProcessorTime.TotalSeconds
        $key = [string]$process.Id
        $delta = if ($Previous.ContainsKey($key)) { [Math]::Max(0.0, $cpu - $Previous[$key]) } else { 0.0 }
        $Previous[$key] = $cpu
        $own = $owned.ContainsKey($process.Id) -or ($measurement -contains $process.ProcessName)
        if (-not $own -and $delta -gt 0) {
            $seconds += $delta
            $byName[$process.ProcessName] = [double]$byName[$process.ProcessName] + $delta
        }
    }

    $gpu = 0.0
    $gpuByName = @{}
    $counters = Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue
    if ($counters) {
        foreach ($counter in $counters.CounterSamples) {
            if ($counter.InstanceName -notmatch 'pid_(\d+)_') {
                continue
            }

            $processId = [int]$Matches[1]
            $name = [string]$names[$processId]
            if ($owned.ContainsKey($processId) -or $name -eq "dwm") {
                continue
            }

            $gpu += $counter.CookedValue
            $gpuByName[$name] = [double]$gpuByName[$name] + $counter.CookedValue
        }
    }

    [pscustomobject]@{
        Seconds = $seconds
        Names = @($byName.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 3 | ForEach-Object { $_.Key })
        GpuPercent = $gpu
        GpuNames = @($gpuByName.GetEnumerator() | Where-Object { $_.Value -ge 1 } | Sort-Object Value -Descending | Select-Object -First 3 | ForEach-Object { $_.Key })
    }
}

# Waits until other processes stay below both limits for thirty seconds.
function Wait-QuietMachine {
    $previous = @{}
    $null = Get-ForeignLoadSample $projectPath $previous $PID
    $last = Get-Date
    $quiet = 0
    $busy = 0
    $deadline = (Get-Date).AddMinutes($QuietTimeoutMinutes)
    while ($quiet -lt 15) {
        Start-Sleep -Seconds 2
        $sample = Get-ForeignLoadSample $projectPath $previous $PID
        $now = Get-Date
        $processors = $sample.Seconds / [Math]::Max(0.5, ($now - $last).TotalSeconds)
        $last = $now
        if ($processors -le $DisturbedProcessors -and $sample.GpuPercent -le $DisturbedGpuPercent) {
            $quiet++
        }
        else {
            # One line when the wait starts and one about every minute after.
            if ($busy % 25 -eq 0) {
                Write-Step ("Waiting: other processes use {0:N1} processors ({1}) and {2:N0} % of the graphics card ({3})." -f
                    $processors, ($sample.Names -join ", "), $sample.GpuPercent, ($sample.GpuNames -join ", "))
            }

            $busy++
            $quiet = 0
        }

        if ($now -gt $deadline) {
            throw "Other programs kept the processor or the graphics card busy for $QuietTimeoutMinutes minutes."
        }
    }
}

# Samples the load of other processes, the whole processor, and the test player's memory into a
# file about every two seconds while a run lasts.
function Start-LoadMonitor([string]$Path) {
    $definitions = "function Get-MeasurementProcessIds { ${function:Get-MeasurementProcessIds} }`nfunction Get-ForeignLoadSample { ${function:Get-ForeignLoadSample} }"
    Start-Job -ArgumentList $projectPath, $Path, $definitions, $PID -ScriptBlock {
        param($OwnProjectPath, $OutputPath, $Definitions, $RootProcessId)
        . ([scriptblock]::Create($Definitions))
        # The invariant culture, so no decimal comma splits a column.
        $invariant = [Globalization.CultureInfo]::InvariantCulture
        $previous = @{}
        $null = Get-ForeignLoadSample $OwnProjectPath $previous $RootProcessId
        $last = [DateTime]::UtcNow
        "utc,foreignProcessors,foreignGpuPercent,processorPercent,playerPrivateMegabytes,playerWorkingSetMegabytes,foreign" | Set-Content -LiteralPath $OutputPath
        while ($true) {
            Start-Sleep -Seconds 2
            $sample = Get-ForeignLoadSample $OwnProjectPath $previous $RootProcessId
            $now = [DateTime]::UtcNow
            $processors = $sample.Seconds / [Math]::Max(0.5, ($now - $last).TotalSeconds)
            $last = $now
            $processor = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
            $player = Get-Process -Name "PlayerWithTests" -ErrorAction SilentlyContinue | Select-Object -First 1
            $private = if ($player) { ($player.PrivateMemorySize64 / 1MB).ToString("F0", $invariant) } else { "" }
            $working = if ($player) { ($player.WorkingSet64 / 1MB).ToString("F0", $invariant) } else { "" }
            $foreign = @($sample.Names) + @($sample.GpuNames | ForEach-Object { "gpu:" + $_ })
            [string]::Format($invariant, "{0:o},{1:F2},{2:F1},{3},{4},{5},{6}", $now, $processors, $sample.GpuPercent, $processor, $private, $working, ($foreign -join " ")) |
                Add-Content -LiteralPath $OutputPath
        }
    }
}

# Stops the load monitor and says why it had stopped by itself, or nothing when it still ran.
function Stop-LoadMonitor($Job) {
    $failure = $null
    if ($Job.State -ne "Running") {
        $child = $Job.ChildJobs[0]
        $reasons = @(@($child.JobStateInfo.Reason) + @($child.Error) | Where-Object { $null -ne $_ } | ForEach-Object { "$_" })
        $failure = "The load monitor had stopped ($($Job.State))" + $(if ($reasons.Count -gt 0) { ": " + $reasons[0] } else { "." })
    }

    Stop-Job $Job
    Remove-Job $Job -Force
    $failure
}

# What other processes used on average within the recorded seconds of a run, the average
# processor use of the machine, and the test player's largest memory. LargestGapSeconds is the
# longest time within the recorded seconds without a sample, the whole recording when it has
# none; without a sample the averages are missing, not zero.
function Measure-Load([string]$Path, [DateTime]$Start, [DateTime]$End) {
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $rows = @()
    if (Test-Path -LiteralPath $Path) {
        foreach ($row in @(Import-Csv -LiteralPath $Path)) {
            # A row that the monitor could not complete is no sample.
            $time = [DateTime]::MinValue
            $processors = 0.0
            $gpu = 0.0
            if (-not [DateTime]::TryParse($row.utc, $invariant, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$time) -or
                -not [double]::TryParse($row.foreignProcessors, [Globalization.NumberStyles]::Float, $invariant, [ref]$processors) -or
                -not [double]::TryParse($row.foreignGpuPercent, [Globalization.NumberStyles]::Float, $invariant, [ref]$gpu)) {
                continue
            }

            if ($time -ge $Start -and $time -le $End.AddSeconds(2)) {
                $rows += [pscustomobject]@{
                    Time = $time
                    foreignProcessors = $processors
                    foreignGpuPercent = $gpu
                    processorPercent = $row.processorPercent
                    playerPrivateMegabytes = $row.playerPrivateMegabytes
                    playerWorkingSetMegabytes = $row.playerWorkingSetMegabytes
                    foreign = $row.foreign
                }
            }
        }
    }

    $largestGap = 0.0
    $last = $Start
    foreach ($row in $rows) {
        $largestGap = [Math]::Max($largestGap, ($row.Time - $last).TotalSeconds)
        $last = $row.Time
    }

    $largestGap = [Math]::Max($largestGap, ($End - $last).TotalSeconds)
    $processors = ($rows | Measure-Object -Property foreignProcessors -Average).Average
    $gpu = ($rows | Measure-Object -Property foreignGpuPercent -Average).Average
    $processor = ($rows | Where-Object { $_.processorPercent } | Measure-Object -Property processorPercent -Average).Average
    $private = ($rows | Where-Object { $_.playerPrivateMegabytes } | Measure-Object -Property playerPrivateMegabytes -Maximum).Maximum
    $working = ($rows | Where-Object { $_.playerWorkingSetMegabytes } | Measure-Object -Property playerWorkingSetMegabytes -Maximum).Maximum
    $names = @($rows | ForEach-Object { $_.foreign -split ' ' } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending | Select-Object -First 5 | ForEach-Object { $_.Name })
    [pscustomobject]@{
        Samples = $rows.Count
        LargestGapSeconds = $largestGap
        ForeignProcessors = $processors
        ForeignGpuPercent = $gpu
        ForeignNames = $names
        ProcessorPercent = if ($null -eq $processor) { 0.0 } else { $processor }
        PlayerPrivateMegabytes = $private
        PlayerWorkingSetMegabytes = $working
    }
}

function Start-Bots([int]$Count) {
    $arguments = @(
        "`"$botsDll`"",
        "--ActiveSimulationBots:AuthorityUrl", "http://127.0.0.1:$gameServicePort",
        "--ActiveSimulationBots:ShardId", $shardId,
        "--ActiveSimulationBots:MinimumActiveBots", "$Count",
        "--ActiveSimulationBots:MaximumActiveBots", "$Count",
        "--ActiveSimulationBots:MinimumOnlineSeconds", "86400",
        "--ActiveSimulationBots:MaximumOnlineSeconds", "86400",
        "--ActiveSimulationBots:StatusIntervalSeconds", "2"
    )
    Start-StackProcess "bots-$Count" "dotnet" $arguments @{ ACTIVE_SIMULATION_BOTS_SECRET = $botSecret } $logRoot $repositoryRoot
}

function Get-JoinedBots([int]$Count) {
    $statusLines = @((Read-SharedText (Join-Path $logRoot "bots-$Count.log")) -split "`n" | Where-Object { $_ -match '^Status: target ' })
    if ($statusLines.Count -eq 0) {
        return 0
    }

    [int]([regex]::Match($statusLines[-1], 'joined (\d+)').Groups[1].Value)
}

function Format-Number($Value, [string]$Format) {
    if ($null -eq $Value -or "$Value" -eq "") { return "-" }
    return ([double]$Value).ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
Write-Step "Logs: $logRoot"
$exitCode = 2
$gameService = $null
$worker = $null
$botProcess = $null
$monitor = $null
$containersToStop = @()
$results = @()
# A secret for this run only, at least 32 characters, never printed.
$botSecret = -join ((1..48) | ForEach-Object { "{0:x}" -f (Get-Random -Minimum 0 -Maximum 16) })
Push-Location $repositoryRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ".env"))) {
        throw "The repository has no .env file; create it from .env.example as docs/LOCAL_DEVELOPMENT.md describes."
    }

    if ($Bots.Count -eq 0 -or @($Bots | Where-Object { $_ -lt 1 -or $_ -gt 900 }).Count -gt 0) {
        throw "Every crowd size of -Bots is 1 to 900, below the worker's connections with room for players."
    }

    if ($Platform -eq "Player") {
        & $runUnityTests -Preflight | ForEach-Object { Write-Step $_ }
    }

    if (Get-TcpListener $gameServicePort) {
        throw "A process already listens on port $gameServicePort. Stop the GameService that runs, then measure again."
    }

    if (Get-UdpListener $udpPort) {
        throw "A process already uses UDP port $udpPort. Stop the stress arena worker that runs, then measure again."
    }

    $composeServices = Get-NativeLines "docker" @("compose", "config", "--services")
    $runningServices = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
    if ($composeServices.ExitCode -ne 0 -or $runningServices.ExitCode -ne 0) {
        throw "Docker Compose cannot read docker-compose.yml or list its containers; is Docker running?"
    }

    $containersToStop = @($composeServices.Lines | Where-Object { $runningServices.Lines -notcontains $_ })
    if ((Invoke-Logged "docker" @("compose", "up", "-d", "--wait") (Join-Path $logRoot "docker-compose-up.log")) -ne 0) {
        Write-LogTail (Join-Path $logRoot "docker-compose-up.log")
        throw "docker compose up failed."
    }

    Write-Step "Building GameService, SimulationWorker, and ActiveSimulationBots."
    foreach ($build in @(
            @("src\Server\GameService\Mmorpg.GameService.csproj", "Release"),
            @("src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj", "Release"),
            @("tools\ActiveSimulationBots\Mmorpg.Tools.ActiveSimulationBots.csproj", "Release"))) {
        $logName = "build-" + [IO.Path]::GetFileNameWithoutExtension($build[0]) + ".log"
        if ((Invoke-Logged "dotnet" @("build", $build[0], "-c", $build[1], "-nologo", "-v", "q") (Join-Path $logRoot $logName)) -ne 0) {
            Write-LogTail (Join-Path $logRoot $logName)
            throw "Building $($build[0]) failed."
        }
    }

    Write-Step "Starting GameService with the development bot endpoint."
    $gameService = Start-StackProcess "game-service" "dotnet" @("`"$gameServiceDll`"") @{
        ASPNETCORE_ENVIRONMENT = "Development"
        ASPNETCORE_URLS = $gameServiceUrl
        DevelopmentSimulationBots__Enabled = "true"
        ACTIVE_SIMULATION_BOTS_SECRET = $botSecret
    } $logRoot $repositoryRoot
    Wait-Until { Test-HttpReady "$gameServiceUrl/health/ready" } "GameService readiness" $gameService (Join-Path $logRoot "game-service.log") $ReadyTimeoutSeconds $logRoot

    Write-Step "Starting the stress arena worker."
    $worker = Start-StackProcess "simulation-worker" "dotnet" @("`"$workerDll`"") @{
        DOTNET_ENVIRONMENT = "Development"
        SIMULATION_WORKER_ID = "local-simulation-worker-2"
        SIMULATION_SHARD_ID = $shardId
        SIMULATION_WORLD_ID = "development-world-1"
        SIMULATION_WORKER_UDP_PORT = "$udpPort"
        SIMULATION_WORKER_ADVERTISED_UDP_PORT = "$udpPort"
    } $logRoot $repositoryRoot
    Wait-Until { (Read-SharedText (Join-Path $logRoot "simulation-worker.log")) -match "Realtime transport listening on UDP port $udpPort" } "The worker's UDP listener" $worker (Join-Path $logRoot "simulation-worker.log") $ReadyTimeoutSeconds $logRoot
    Wait-Until { Test-ShardOnline $gameServiceUrl $shardId } "The registration of $shardId" $worker (Join-Path $logRoot "simulation-worker.log") $ReadyTimeoutSeconds $logRoot

    $exitCode = 0
    foreach ($count in $Bots) {
        Write-Step "Starting $count ActiveSimulationBots on $shardId."
        $botProcess = Start-Bots $count
        Wait-Until { (Get-JoinedBots $count) -ge $count } "$count joined bots" $botProcess (Join-Path $logRoot "bots-$count.log") $ReadyTimeoutSeconds $logRoot
        Write-Step "All $count bots joined."

        if ($Platform -eq "Editor") {
            Write-Step "The stack and $count bots stay up for $HoldMinutes min. In the open Editor, run LiveStackCrowdPlayModeTests from the Test Runner; it measures the crowd it finds and prints [CROWD] lines. Press Ctrl+C to end earlier."
            $holdEnd = (Get-Date).AddMinutes($HoldMinutes)
            while ((Get-Date) -lt $holdEnd -and -not $botProcess.HasExited) {
                Start-Sleep -Seconds 2
            }

            break
        }

        $measured = $false
        for ($attempt = 1; $attempt -le $Attempts -and -not $measured; $attempt++) {
            Write-Step "Waiting until no other program uses the processor or the graphics card."
            Wait-QuietMachine
            $label = "player-$count-$attempt"
            $runFolder = Join-Path $logRoot $label
            New-Item -ItemType Directory -Force -Path $runFolder | Out-Null
            $loadPath = Join-Path $runFolder "load.csv"
            $monitor = Start-LoadMonitor $loadPath
            [Environment]::SetEnvironmentVariable("MMORPG_CROWD_BOTS", "$count")
            [Environment]::SetEnvironmentVariable("MMORPG_CROWD_SECONDS", "$Seconds")
            [Environment]::SetEnvironmentVariable("MMORPG_CROWD_RESULTS", $runFolder)
            [Environment]::SetEnvironmentVariable("MMORPG_CROWD_LABEL", $label)
            Write-Step "Measuring $count bots in an IL2CPP test player that compiles like a $($PlayerBuild.ToLowerInvariant()) build, attempt $attempt."
            $testPassed = $false
            $monitorFailure = $null
            try {
                & $runUnityTests -Platform StandaloneWindows64 -PlayerBuild $PlayerBuild -TestCategory $category -ResultsPath $runFolder | ForEach-Object { Write-Step $_ }
                $testPassed = $true
            }
            catch {
                Write-Step "The crowd run failed: $($_.Exception.Message)"
            }
            finally {
                foreach ($name in $crowdVariables) {
                    [Environment]::SetEnvironmentVariable($name, $null)
                }

                $stopping = $monitor
                $monitor = $null
                $monitorFailure = Stop-LoadMonitor $stopping
            }

            # The test player logs to its own file, which the next player start replaces.
            $playerLog = Get-UnityPlayerLogPath $projectPath
            if (Test-Path -LiteralPath $playerLog) {
                Copy-Item -LiteralPath $playerLog -Destination (Join-Path $runFolder "player.log") -Force
            }

            # The test writes its result before its last checks, so a result alone proves nothing.
            if (-not $testPassed) {
                Write-Step "The crowd test did not pass, so the attempt does not count; its logs are in '$runFolder'."
                continue
            }

            $resultPath = Join-Path $runFolder "crowd-$label.json"
            if (-not (Test-Path -LiteralPath $resultPath)) {
                Write-Step "The run wrote no result; its logs are in '$runFolder'."
                continue
            }

            if ($null -ne $monitorFailure) {
                Write-Step $monitorFailure
            }

            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $start = [DateTime]::Parse($result.recordingStartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
            $end = [DateTime]::Parse($result.recordingEndUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
            $load = Measure-Load $loadPath $start $end
            if ($load.Samples -eq 0 -or $load.LargestGapSeconds -gt $LoadSampleGapSeconds) {
                Write-Step ("Attempt {0}: the samples of what other programs used do not cover the recording ({1} samples, {2:N0} s without one), so nothing shows that it was undisturbed; repeating it." -f $attempt, $load.Samples, $load.LargestGapSeconds)
                continue
            }

            Write-Step ("Attempt {0}: other processes used {1:N2} processors and {2:N1} % of the graphics card on average while it recorded ({3}); the machine was {4:N0} % busy." -f $attempt, $load.ForeignProcessors, $load.ForeignGpuPercent, ($(if ($load.ForeignNames.Count -gt 0) { $load.ForeignNames -join ", " } else { "none" })), $load.ProcessorPercent)
            if ($load.ForeignProcessors -gt $DisturbedProcessors -or $load.ForeignGpuPercent -gt $DisturbedGpuPercent) {
                Write-Step "Another process disturbed the measurement; repeating it."
                continue
            }

            $measured = $true
            $results += [pscustomobject]@{ Bots = $count; Result = $result; Load = $load; Folder = $runFolder }
        }

        if (-not $measured) {
            Write-Step "The crowd of $count bots could not be measured, with a passing test and undisturbed, in $Attempts attempts."
            $exitCode = 1
        }

        Write-Step "Stopping the $count bots and waiting until their bodies are gone."
        Stop-StackProcess $botProcess "ActiveSimulationBots"
        $botProcess = $null
        Start-Sleep -Seconds 25
    }
}
catch {
    Write-Step "The crowd could not be measured: $($_.Exception.Message)"
    $exitCode = 2
}
finally {
    if ($null -ne $monitor) {
        $null = Stop-LoadMonitor $monitor
    }

    foreach ($name in $crowdVariables) {
        [Environment]::SetEnvironmentVariable($name, $null)
    }

    Stop-StackProcess $botProcess "ActiveSimulationBots"
    Stop-StackProcess $worker "SimulationWorker"
    Stop-StackProcess $gameService "GameService"
    if ($containersToStop.Count -gt 0) {
        $nowRunning = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
        $toStop = @($containersToStop | Where-Object { $nowRunning.Lines -contains $_ })
        if ($toStop.Count -gt 0) {
            $null = Invoke-Logged "docker" (@("compose", "stop") + $toStop) (Join-Path $logRoot "docker-compose-stop.log")
            Write-Step "Stopped the containers $($toStop -join ', ')."
        }
    }

    Pop-Location
}

if ($results.Count -gt 0) {
    $first = $results[0].Result
    $table = @(
        "Crowd baseline, $($first.runtime) $($first.platform), $($first.screenWidth) by $($first.screenHeight), $($first.graphicsDevice), $($first.processor)",
        "| Bots | Shown after | Frame p50 / p95 / p99 / max | Owner snapshots per s | Remote starved | Allocated / reserved / managed | Player private memory | Machine busy |",
        "| ---: | ---: | --- | ---: | --- | --- | ---: | ---: |"
    )
    foreach ($entry in $results) {
        $r = $entry.Result
        $s = $r.summary
        $table += ("| {0} | {1} s | {2} / {3} / {4} / {5} ms | {6} | {7} %, {8} times, longest {9} s | {10} / {11} / {12} MB | {13} MB | {14} % |" -f
            $entry.Bots,
            (Format-Number $r.crowdShownSeconds "0.0"),
            (Format-Number $s.frameMilliseconds.p50 "0.0"), (Format-Number $s.frameMilliseconds.p95 "0.0"),
            (Format-Number $s.frameMilliseconds.p99 "0.0"), (Format-Number $s.frameMilliseconds.maximum "0.0"),
            (Format-Number $r.ownerSnapshotsPerSecond "0.0"),
            (Format-Number $s.remoteStarvedPercent "0.00"), $s.remoteStarvations, (Format-Number $s.longestRemoteStarvationSeconds "0.00"),
            (Format-Number $r.allocatedMegabytes "0"), (Format-Number $r.reservedMegabytes "0"), (Format-Number $r.managedHeapMegabytes "0"),
            (Format-Number $entry.Load.PlayerPrivateMegabytes "0"),
            (Format-Number $entry.Load.ProcessorPercent "0"))
    }

    $table | ForEach-Object { Write-Host $_ }
    [IO.File]::WriteAllLines((Join-Path $logRoot "crowd-summary.md"), [string[]]$table)
}

$verdict = if ($exitCode -eq 0 -and $Platform -eq "Editor") { "held for the Editor" } elseif ($exitCode -eq 0) { "measured" } elseif ($exitCode -eq 1) { "FAILED" } else { "could not run" }
Write-Step "Client crowd $verdict. Logs: $logRoot"
exit $exitCode

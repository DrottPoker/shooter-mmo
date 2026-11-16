# Measures how the Unity client copes with a crowd: the crowd baseline of the client. It starts
# a local stack with the stress arena, local-shard-2 with Development World 1, where every player
# stays inside one interest radius, and runs the ClientCrowd PlayMode test, which enters the arena
# as a player, for each kind of measurement that -Only names, every kind by default:
#
# - Standing and Arrival: for each size of -Bots it starts that many ActiveSimulationBots, waits
#   until all have joined, and lets the test record the frames from standing in the arena until
#   the whole crowd is shown, the arrival, and, with Standing, the client measurements for
#   -Seconds with the whole crowd shown. With Arrival the test then asks the script to stop the
#   bots, reads the memory once their bodies are gone, asks for the same bots again, and records
#   their arrival and the memory a third time.
# - Churn: -ChurnBots bot slots log in and out at about three players a second while the test
#   records for -Seconds.
# - Mobs: for each size of -Mobs, a worker on a World variant with that many walking Mobs and no
#   bots, written with StackStressGenerator world-variant from tools/load/client-crowd-mobs.json;
#   the test records their arrival and -Seconds with all of them shown.
#
# It stops the bots and waits until their bodies are gone before the next size.
#
# -Platform Player runs the test in a Windows IL2CPP test player that compiles like a release
# build, in a 1920 by 1080 window on the desktop, as tools/Run-UnityTests.ps1 builds it. Before
# each run it waits until other programs, such as another Unity Editor, a build, or a game, have
# used at most two and a half processors and 10 % of the graphics card's 3D engine for thirty
# seconds, samples them every few seconds during the run, and repeats a run whose recorded
# seconds they disturbed, whose test failed, or whose samples do not cover every recording, so no
# result stands without proof that nothing else used the machine. -Platform Editor starts the
# stack and the bots of the first size and keeps them for -HoldMinutes, for a person who runs the
# test in an open Editor, whose Play Mode needs its window for honest frame times; the headless
# Editor draws nothing.
#
# GameService gets the development bot endpoint with a secret that the script makes for this run
# and passes to GameService and the bots only, so the .env file is not read or changed. The script
# stops everything it started, also when a run fails or it is interrupted with Ctrl+C, and refuses
# to run while GameService or the stress arena's ports are in use or, with -Platform Player, a
# Unity Editor has the project open. The logs and the result of every run stay in a folder below
# the system temp directory, which the script prints with the tables of crowd-summary.md.
#
# Exit codes: 0 every measurement was made, 1 a measurement failed, 2 the stack or Unity could not
# run it.
param(
    [ValidateSet("Player", "Editor")]
    [string]$Platform = "Player",
    # The crowd sizes of Standing and Arrival, measured in this order.
    [string[]]$Bots = @("100", "200", "400"),
    # How long the standing, churn, and Mob recordings last.
    [int]$Seconds = 60,
    # The kinds of measurement to make, every kind by default: Standing, Arrival, Churn, Mobs.
    [string[]]$Only = @(),
    # The bot slots of the churn.
    [int]$ChurnBots = 200,
    # The walking Mobs of each Mob measurement, measured in this order.
    [string[]]$Mobs = @("100", "200"),
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
$worldId = "development-world-1"
$udpPort = 27016
$category = "ClientCrowd"
# Release builds, so the services and the bots, which share the machine with the client, take
# as little of it as they can.
$gameServiceDll = Join-Path $repositoryRoot "src\Server\GameService\bin\Release\net10.0\Mmorpg.GameService.dll"
$workerDll = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Release\net10.0\Mmorpg.SimulationWorker.dll"
$botsDll = Join-Path $repositoryRoot "tools\ActiveSimulationBots\bin\Release\net10.0\Mmorpg.Tools.ActiveSimulationBots.dll"
$generatorExe = Join-Path $repositoryRoot "tools\StackStressGenerator\bin\Release\net10.0\Mmorpg.Tools.StackStressGenerator.exe"
$mobTemplatePath = Join-Path $repositoryRoot "tools\load\client-crowd-mobs.json"
# The variables the crowd test reads.
$crowdVariables = @("MMORPG_CROWD_MODE", "MMORPG_CROWD_BOTS", "MMORPG_CROWD_SECONDS", "MMORPG_CROWD_RESULTS", "MMORPG_CROWD_LABEL",
    "MMORPG_CROWD_STANDING", "MMORPG_CROWD_RETURN", "MMORPG_CROWD_MOBS", "MMORPG_CROWD_MOB_ACTOR")
# How long the bodies of stopped bots stay, 20 s, and a margin.
$bodySeconds = 25
# The clean logouts after which the churn runs steadily.
$churnWarmLogouts = 20

$StackStepPrefix = "crowd"
. (Join-Path $PSScriptRoot "LocalStackCommon.ps1")
. (Join-Path $PSScriptRoot "UnityCommon.ps1")
. (Join-Path $PSScriptRoot "ClientCrowdCommon.ps1")

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

    # Without an answer of the counters the use of the graphics card is unknown, not zero. An
    # engine that ended between the listing and the reading only costs its own value.
    $gpu = $null
    $gpuByName = @{}
    $counters = Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue
    if ($counters -and @($counters.CounterSamples).Count -gt 0) {
        $gpu = 0.0
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

# Waits until other processes stay below both limits for thirty seconds. A sample without the
# graphics card's use is not quiet.
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
        if ($processors -le $DisturbedProcessors -and $null -ne $sample.GpuPercent -and $sample.GpuPercent -le $DisturbedGpuPercent) {
            $quiet++
        }
        else {
            # One line when the wait starts and one about every minute after.
            if ($busy % 25 -eq 0 -and $null -eq $sample.GpuPercent) {
                Write-Step ("Waiting: the use of the graphics card cannot be read from the counter 'GPU Engine'; other processes use {0:N1} processors ({1})." -f
                    $processors, ($sample.Names -join ", "))
            }
            elseif ($busy % 25 -eq 0) {
                Write-Step ("Waiting: other processes use {0:N1} processors ({1}) and {2:N0} % of the graphics card ({3})." -f
                    $processors, ($sample.Names -join ", "), $sample.GpuPercent, ($sample.GpuNames -join ", "))
            }

            $busy++
            $quiet = 0
        }

        if ($now -gt $deadline) {
            throw "Other programs kept the processor or the graphics card busy for $QuietTimeoutMinutes minutes, or the use of the graphics card could not be read."
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

# The last status line of a bot process: how many bots joined, and how many logged out cleanly
# since it started.
function Get-BotStatus([string]$Name) {
    $statusLines = @((Read-SharedText (Join-Path $logRoot "$Name.log")) -split "`n" | Where-Object { $_ -match '^Status: target ' })
    if ($statusLines.Count -eq 0) {
        return [pscustomobject]@{ Joined = 0; CleanLogouts = 0 }
    }

    $line = $statusLines[-1]
    [pscustomobject]@{
        Joined = [int]([regex]::Match($line, 'joined (\d+)').Groups[1].Value)
        CleanLogouts = [int]([regex]::Match($line, 'clean logouts (\d+)').Groups[1].Value)
    }
}

# Starts the bots of a kind, Standing for a crowd that stays or Churn, under a log of their own.
function Start-Bots([string]$Kind, [int]$Count) {
    Stop-Bots
    $script:botStarts++
    $script:botName = "bots-" + $Kind.ToLowerInvariant() + "-$Count-$($script:botStarts)"
    $arguments = @(
        "`"$botsDll`"",
        "--ActiveSimulationBots:AuthorityUrl", "http://127.0.0.1:$gameServicePort",
        "--ActiveSimulationBots:ShardId", $shardId
    ) + @(ConvertTo-BotArguments (Get-CrowdBotSettings $Kind $Count))
    $script:botProcess = Start-StackProcess $script:botName "dotnet" $arguments @{ ACTIVE_SIMULATION_BOTS_SECRET = $botSecret } $logRoot $repositoryRoot
}

# Stops the bots with Ctrl+C, so they leave as players do; their bodies stay 20 s.
function Stop-Bots {
    if ($null -ne $script:botProcess) {
        Stop-StackProcess $script:botProcess "ActiveSimulationBots"
        $script:botProcess = $null
        $script:botsStoppedAt = [DateTime]::UtcNow
    }
}

# The bodies of bots that stopped a moment ago would count as the next crowd.
function Wait-BotBodiesGone {
    $wait = $bodySeconds - ([DateTime]::UtcNow - $script:botsStoppedAt).TotalSeconds
    if ($wait -gt 0) {
        Write-Step "Waiting until the bodies of the stopped bots are gone."
        Start-Sleep -Seconds ([int][Math]::Ceiling($wait))
    }
}

# Keeps the bots of a kind that run with at least JoinedAtLeast joined, and starts them anew
# otherwise; the churn then waits until bots come and go steadily.
function Initialize-Bots([string]$Kind, [int]$Count, [int]$JoinedAtLeast) {
    if ($null -ne $script:botProcess -and -not $script:botProcess.HasExited -and
        (Get-BotStatus $script:botName).Joined -ge $JoinedAtLeast) {
        return
    }

    Stop-Bots
    Wait-BotBodiesGone
    Write-Step ("Starting {0} ActiveSimulationBots on {1}{2}." -f $Count, $shardId, $(if ($Kind -eq "Churn") { " that come and go" } else { "" }))
    Start-Bots $Kind $Count
    $name = $script:botName
    $log = Join-Path $logRoot "$name.log"
    Wait-Until { (Get-BotStatus $name).Joined -ge $JoinedAtLeast } "$JoinedAtLeast joined bots" $script:botProcess $log $ReadyTimeoutSeconds $logRoot
    if ($Kind -eq "Churn") {
        Wait-Until { (Get-BotStatus $name).CleanLogouts -ge $churnWarmLogouts } "$churnWarmLogouts clean bot logouts" $script:botProcess $log $ReadyTimeoutSeconds $logRoot
        Write-Step "The bots come and go."
    }
    else {
        Write-Step "All $Count bots joined."
    }
}

# Runs the stress arena worker on the checked-in World, or on the World data of a variant, and
# keeps one that runs already on the same World.
function Use-ArenaWorker([string]$Name, [string]$WorldDataPath) {
    if ($null -ne $script:worker -and -not $script:worker.HasExited -and $script:workerWorld -eq $WorldDataPath) {
        return
    }

    Stop-ArenaWorker
    Write-Step $(if ($WorldDataPath) { "Starting the stress arena worker on the World variant." } else { "Starting the stress arena worker." })
    $environment = @{
        DOTNET_ENVIRONMENT = "Development"
        SIMULATION_WORKER_ID = "local-simulation-worker-2"
        SIMULATION_SHARD_ID = $shardId
        SIMULATION_WORLD_ID = $worldId
        SIMULATION_WORKER_UDP_PORT = "$udpPort"
        SIMULATION_WORKER_ADVERTISED_UDP_PORT = "$udpPort"
    }
    if ($WorldDataPath) {
        $environment["SimulationWorker__WorldDataPath"] = $WorldDataPath
    }

    $script:worker = Start-StackProcess $Name "dotnet" @("`"$workerDll`"") $environment $logRoot $repositoryRoot
    $script:workerWorld = $WorldDataPath
    $log = Join-Path $logRoot "$Name.log"
    Wait-Until { (Read-SharedText $log) -match "Realtime transport listening on UDP port $udpPort" } "The worker's UDP listener" $script:worker $log $ReadyTimeoutSeconds $logRoot
    Wait-Until { Test-ShardOnline $gameServiceUrl $shardId } "The registration of $shardId" $script:worker $log $ReadyTimeoutSeconds $logRoot
}

function Stop-ArenaWorker {
    if ($null -ne $script:worker) {
        Stop-StackProcess $script:worker "SimulationWorker"
        $script:worker = $null
        $script:workerWorld = $null
    }
}

# Writes the World variant with that many walking Mobs into the run's log folder and returns
# the folder the worker reads. A variant that changes the collision revision cannot be measured:
# the client refuses to join it.
function New-MobVariant([int]$Count) {
    $folder = Join-Path $logRoot "mobs-$Count"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $scenarioPath = Join-Path $folder "client-crowd-mobs-$Count.json"
    [IO.File]::WriteAllText($scenarioPath, (New-MobVariantScenario $mobTemplate $Count))
    $log = Join-Path $folder "world-variant-command.log"
    $exit = Invoke-Logged $generatorExe @("world-variant", "--scenario", $scenarioPath, "--run-folder", $folder) $log
    $output = (Read-SharedText $log).Trim()
    if ($exit -ne 0) {
        throw "The World variant with $Count Mobs could not be written: $output"
    }

    $output -split "`r?`n" | Where-Object { $_ } | ForEach-Object { Write-Step $_ }
    if ($output -notmatch "keeps the checked-in collision revision") {
        throw "The World variant with $Count Mobs changes the collision revision, so the client would refuse to join it."
    }

    Join-Path $folder "world-variant\worker"
}

# Answers a request of the crowd test: the bots leave, or the same bots come again.
function Invoke-CrowdRequest($Request, [string]$Mode, [int]$Count) {
    if ($Mode -eq "crowd" -and $Request.Request -eq "depart") {
        Write-Step "The test asks the bots to leave; stopping them."
        Stop-Bots
    }
    elseif ($Mode -eq "crowd" -and $Request.Request -eq "return") {
        Write-Step "The test asks for the bots again; starting $Count bots."
        Start-Bots "Standing" $Count
    }
    else {
        Write-Step "The test asked for '$($Request.Request)', which a $Mode run does not answer."
    }
}

# Runs the crowd test in a test player as tools/Run-UnityTests.ps1 does, in a runspace of this
# process, so the script answers the test's requests while it runs and Unity and the player stay
# processes of the measurement. Throws when the test failed.
function Invoke-CrowdUnityRun([string]$RunFolder, [string]$Mode, [int]$Count) {
    $runner = [PowerShell]::Create()
    $noInput = New-Object 'System.Management.Automation.PSDataCollection[psobject]'
    $noInput.Complete()
    $output = New-Object 'System.Management.Automation.PSDataCollection[psobject]'
    $shown = 0
    try {
        $null = $runner.AddCommand($runUnityTests)
        $null = $runner.AddParameter("Platform", [string[]]@("StandaloneWindows64"))
        $null = $runner.AddParameter("PlayerBuild", $PlayerBuild)
        $null = $runner.AddParameter("TestCategory", $category)
        $null = $runner.AddParameter("ResultsPath", $RunFolder)
        $async = $runner.BeginInvoke($noInput, $output)
        while (-not $async.IsCompleted) {
            while ($shown -lt $output.Count) {
                Write-Step "$($output[$shown])"
                $shown++
            }

            foreach ($request in @(Get-PendingCrowdRequests $RunFolder)) {
                Invoke-CrowdRequest $request $Mode $Count
                Complete-CrowdRequest $request
            }

            Start-Sleep -Milliseconds 500
        }

        try {
            $null = $runner.EndInvoke($async)
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) {
                $exception = $exception.InnerException
            }

            throw $exception.Message
        }
    }
    finally {
        while ($shown -lt $output.Count) {
            Write-Step "$($output[$shown])"
            $shown++
        }

        # An interrupted script stops the run, whose own cleanup ends the Unity it started.
        if ($runner.InvocationStateInfo.State -eq [System.Management.Automation.PSInvocationState]::Running) {
            $runner.Stop()
        }

        $runner.Dispose()
    }
}

# Runs the crowd test of one kind and size until an attempt counts, and returns that attempt's
# entry for the summary, or nothing when none counted. Kind is Standing for a crowd of bots,
# with Standing for its standing recording and ComeBack for its departure and return, Churn, or
# Mobs.
function Measure-CrowdKind([string]$Kind, [int]$Count, [bool]$Standing, [bool]$ComeBack) {
    $mode = if ($Kind -eq "Churn") { "churn" } elseif ($Kind -eq "Mobs") { "mobs" } else { "crowd" }
    $churnMinimum = (Get-CrowdBotSettings "Churn" $Count).MinimumActiveBots
    $what = if ($mode -eq "churn") { "$Count bot slots that come and go" }
    elseif ($mode -eq "mobs") { "$Count walking Mobs" }
    elseif ($ComeBack) { "$Count bots, their departure, and their return" }
    else { "$Count bots" }
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if ($mode -eq "crowd") {
            Initialize-Bots "Standing" $Count $Count
        }
        elseif ($mode -eq "churn") {
            Initialize-Bots "Churn" $Count $churnMinimum
        }
        elseif ($null -eq $script:worker -or $script:worker.HasExited) {
            throw "The worker of the Mob variant ended; its log is in '$logRoot'."
        }

        Write-Step "Waiting until no other program uses the processor or the graphics card."
        Wait-QuietMachine
        $label = if ($mode -eq "crowd") { "player-$Count-$attempt" } else { "player-$mode-$Count-$attempt" }
        $runFolder = Join-Path $logRoot $label
        New-Item -ItemType Directory -Force -Path $runFolder | Out-Null
        $loadPath = Join-Path $runFolder "load.csv"
        $script:monitor = Start-LoadMonitor $loadPath
        $values = @{
            MMORPG_CROWD_MODE = $mode
            MMORPG_CROWD_BOTS = $(if ($mode -eq "crowd") { "$Count" } elseif ($mode -eq "churn") { "$churnMinimum" } else { "0" })
            MMORPG_CROWD_SECONDS = "$Seconds"
            MMORPG_CROWD_RESULTS = $runFolder
            MMORPG_CROWD_LABEL = $label
            MMORPG_CROWD_STANDING = $(if ($Standing) { "1" } else { "0" })
            MMORPG_CROWD_RETURN = $(if ($ComeBack) { "1" } else { "0" })
            MMORPG_CROWD_MOBS = $(if ($mode -eq "mobs") { "$Count" } else { "0" })
            MMORPG_CROWD_MOB_ACTOR = $mobActor
        }
        foreach ($name in $values.Keys) {
            [Environment]::SetEnvironmentVariable($name, $values[$name])
        }

        Write-Step "Measuring $what in an IL2CPP test player that compiles like a $($PlayerBuild.ToLowerInvariant()) build, attempt $attempt."
        $testPassed = $false
        $monitorFailure = $null
        try {
            Invoke-CrowdUnityRun $runFolder $mode $Count
            $testPassed = $true
        }
        catch {
            Write-Step "The crowd run failed: $($_.Exception.Message)"
        }
        finally {
            foreach ($name in $crowdVariables) {
                [Environment]::SetEnvironmentVariable($name, $null)
            }

            $stopping = $script:monitor
            $script:monitor = $null
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

        # The test fails when it shows fewer bots while it records; this is the same check from
        # the side of the bots, of those that came back after a departure too.
        if ($mode -eq "mobs") {
            if ($script:worker.HasExited) {
                Write-Step "Attempt ${attempt}: the worker of the Mob variant has ended, so the attempt does not count."
                Write-LogTail (Join-Path $logRoot "simulation-worker-mobs-$Count.log")
                break
            }
        }
        else {
            $ended = $null -eq $script:botProcess -or $script:botProcess.HasExited
            $joinedNow = if ($null -eq $script:botName) { 0 } else { (Get-BotStatus $script:botName).Joined }
            $needed = if ($mode -eq "churn") { 1 } else { $Count }
            if ($ended -or $joinedNow -lt $needed) {
                Write-Step "Attempt ${attempt}: the bots did not stay ($joinedNow joined, the bot process $(if ($ended) { 'has ended' } else { 'runs' })), so the attempt does not count."
                if ($ended) {
                    Write-LogTail (Join-Path $logRoot "$($script:botName).log")
                    break
                }

                continue
            }
        }

        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $windows = @()
        if ($result.arrival.measured) {
            $windows += [pscustomobject]@{ Key = "Arrival"; Name = "arrival"; Start = (ConvertFrom-UtcText $result.arrival.startUtc); End = (ConvertFrom-UtcText $result.arrival.endUtc); After = $LoadSampleGapSeconds }
        }

        if ($result.standing) {
            $windows += [pscustomobject]@{ Key = "Standing"; Name = "$(if ($mode -eq 'crowd') { 'standing' } else { $mode }) recording"; Start = (ConvertFrom-UtcText $result.recordingStartUtc); End = (ConvertFrom-UtcText $result.recordingEndUtc); After = 2.0 }
        }

        if ($result.returnArrival.measured) {
            $windows += [pscustomobject]@{ Key = "Return"; Name = "return"; Start = (ConvertFrom-UtcText $result.returnArrival.startUtc); End = (ConvertFrom-UtcText $result.returnArrival.endUtc); After = $LoadSampleGapSeconds }
        }

        if ($windows.Count -eq 0) {
            Write-Step "Attempt ${attempt}: the test recorded nothing, so the attempt does not count."
            continue
        }

        $loads = @{}
        foreach ($window in $windows) {
            $load = Measure-Load $loadPath $window.Start $window.End $window.After
            $loads[$window.Key] = $load
            $window | Add-Member -NotePropertyName Load -NotePropertyValue $load
            Write-Step ("Attempt {0}, {1}: other processes used {2:N2} processors and {3:N1} % of the graphics card on average ({4}); the machine was {5:N0} % busy." -f
                $attempt, $window.Name, $load.ForeignProcessors, $load.ForeignGpuPercent,
                ($(if ($load.ForeignNames.Count -gt 0) { $load.ForeignNames -join ", " } else { "none" })), $load.ProcessorPercent)
        }

        $problem = Get-LoadProblem $windows $LoadSampleGapSeconds $DisturbedProcessors $DisturbedGpuPercent
        if ($null -ne $problem) {
            Write-Step "Attempt ${attempt}: $problem; repeating it."
            continue
        }

        $private = New-Object System.Collections.Generic.List[object]
        foreach ($memory in @($result.memory)) {
            $private.Add((Get-PrivateMemoryNear $loadPath (ConvertFrom-UtcText $memory.utc)))
        }

        return [pscustomobject]@{ Kind = $mode; Count = $Count; Result = $result; Loads = $loads; PrivateMemory = $private.ToArray(); Folder = $runFolder }
    }

    Write-Step "The measurement of $what could not be made, with a passing test, the whole crowd, and undisturbed, in $Attempts attempts."
}

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
Write-Step "Logs: $logRoot"
$exitCode = 2
$gameService = $null
$script:worker = $null
$script:workerWorld = $null
$script:botProcess = $null
$script:botName = $null
$script:botStarts = 0
$script:botsStoppedAt = [DateTime]::MinValue
$script:monitor = $null
$containersToStop = @()
$results = @()
$kinds = $CrowdKinds
# A secret for this run only, at least 32 characters, never printed.
$botSecret = -join ((1..48) | ForEach-Object { "{0:x}" -f (Get-Random -Minimum 0 -Maximum 16) })
Push-Location $repositoryRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ".env"))) {
        throw "The repository has no .env file; create it from .env.example as docs/LOCAL_DEVELOPMENT.md describes."
    }

    $botSizes = @(ConvertTo-CrowdNumbers $Bots "-Bots, the crowd sizes below the worker's connections with room for players," 1 900)
    $mobSizes = @(ConvertTo-CrowdNumbers $Mobs "-Mobs, four areas of at most 256 Mobs each," 1 1024)
    $kinds = @(ConvertTo-CrowdKinds $Only)
    if ($ChurnBots -lt 2 -or $ChurnBots -gt 900) {
        throw "-ChurnBots is 2 to 900, below the worker's connections with room for players."
    }

    $mobTemplate = [IO.File]::ReadAllText($mobTemplatePath)
    $mobActor = Get-MobVariantActor $mobTemplate
    $measureCrowd = $kinds -contains "Standing" -or $kinds -contains "Arrival"
    $needsBots = $Platform -eq "Editor" -or $measureCrowd -or $kinds -contains "Churn"
    $needsVariant = $Platform -eq "Player" -and $kinds -contains "Mobs"
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

    $builds = @("src\Server\GameService\Mmorpg.GameService.csproj", "src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj")
    if ($needsBots) {
        $builds += "tools\ActiveSimulationBots\Mmorpg.Tools.ActiveSimulationBots.csproj"
    }

    if ($needsVariant) {
        $builds += "tools\StackStressGenerator\Mmorpg.Tools.StackStressGenerator.csproj"
    }

    Write-Step "Building $(($builds | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) -replace '^Mmorpg\.(Tools\.)?', '' }) -join ', ')."
    foreach ($build in $builds) {
        $logName = "build-" + [IO.Path]::GetFileNameWithoutExtension($build) + ".log"
        if ((Invoke-Logged "dotnet" @("build", $build, "-c", "Release", "-nologo", "-v", "q") (Join-Path $logRoot $logName)) -ne 0) {
            Write-LogTail (Join-Path $logRoot $logName)
            throw "Building $build failed."
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

    $exitCode = 0
    if ($Platform -eq "Editor") {
        Use-ArenaWorker "simulation-worker" $null
        Initialize-Bots "Standing" $botSizes[0] $botSizes[0]
        Write-Step "The stack and $($botSizes[0]) bots stay up for $HoldMinutes min. In the open Editor, run LiveStackCrowdPlayModeTests from the Test Runner; it measures the crowd it finds and prints [CROWD] lines. Press Ctrl+C to end earlier."
        $holdEnd = (Get-Date).AddMinutes($HoldMinutes)
        while ((Get-Date) -lt $holdEnd -and -not $script:botProcess.HasExited) {
            Start-Sleep -Seconds 2
        }
    }
    else {
        if ($measureCrowd) {
            Use-ArenaWorker "simulation-worker" $null
            foreach ($count in $botSizes) {
                $entry = Measure-CrowdKind "Standing" $count ($kinds -contains "Standing") ($kinds -contains "Arrival") | Select-Object -Last 1
                if ($null -ne $entry) {
                    $results += $entry
                }
                else {
                    $exitCode = 1
                }

                Write-Step "Stopping the $count bots and waiting until their bodies are gone."
                Stop-Bots
                Wait-BotBodiesGone
            }
        }

        if ($kinds -contains "Churn") {
            Use-ArenaWorker "simulation-worker" $null
            $entry = Measure-CrowdKind "Churn" $ChurnBots $true $false | Select-Object -Last 1
            if ($null -ne $entry) {
                $results += $entry
            }
            else {
                $exitCode = 1
            }

            Write-Step "Stopping the bots that come and go and waiting until their bodies are gone."
            Stop-Bots
            Wait-BotBodiesGone
        }

        if ($kinds -contains "Mobs") {
            foreach ($count in $mobSizes) {
                Write-Step "Writing the World variant with $count walking Mobs."
                Use-ArenaWorker "simulation-worker-mobs-$count" (New-MobVariant $count)
                $entry = Measure-CrowdKind "Mobs" $count $true $false | Select-Object -Last 1
                if ($null -ne $entry) {
                    $results += $entry
                }
                else {
                    $exitCode = 1
                }
            }
        }
    }
}
catch {
    Write-Step "The crowd could not be measured: $($_.Exception.Message)"
    $exitCode = 2
}
finally {
    if ($null -ne $script:monitor) {
        $null = Stop-LoadMonitor $script:monitor
    }

    foreach ($name in $crowdVariables) {
        [Environment]::SetEnvironmentVariable($name, $null)
    }

    Stop-Bots
    Stop-ArenaWorker
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
    $table = @(New-CrowdSummary $results $kinds)
    $table | ForEach-Object { Write-Host $_ }
    [IO.File]::WriteAllLines((Join-Path $logRoot "crowd-summary.md"), [string[]]$table)
}

$verdict = if ($exitCode -eq 0 -and $Platform -eq "Editor") { "held for the Editor" } elseif ($exitCode -eq 0) { "measured" } elseif ($exitCode -eq 1) { "FAILED" } else { "could not run" }
Write-Step "Client crowd $verdict. Logs: $logRoot"
exit $exitCode

# Runs the live stack test: the LiveStack PlayMode test of the Unity client, which joins a real
# SimulationWorker through the client's real network layer, against a local stack that this
# script starts. It starts PostgreSQL and Redis with Docker Compose when they do not run, builds
# GameService and SimulationWorker and starts them in the Development environment as background
# processes, waits until GameService is ready and the worker listens and has registered its
# Shard, and runs only the LiveStack tests headless through tools/Run-UnityTests.ps1.
#
# With -Network <profile> it puts tools/NetworkImpairmentProxy with a profile of
# tools/load/network-profiles between the client and the worker, which advertises the proxy's
# port, and runs only the network part of the test, the LiveStackNetwork category, which walks,
# stands, opens an NPC, and leaves and comes back -Leaves times while a second connection on the
# worker's own port walks beside it as another player. Without a profile it runs the whole
# LiveStack category, the network part included, on loopback.
#
# It always stops the two services, with Ctrl+C so the worker saves its characters and goes
# offline, the proxy, and the containers it started, also when the test fails or the script is
# interrupted with Ctrl+C. It never touches containers or processes it did not start, and it
# refuses to run while GameService, a worker, or a proxy already uses its ports or a Unity Editor
# has the project open. The logs of a run stay in a folder below the system temp directory, which
# the script prints, with the result file of the network part.
#
# Exit codes: 0 the scenario passed, 1 the test failed, 2 the stack or Unity could not run it.
param(
    # How long GameService and the worker may take to become ready.
    [int]$ReadyTimeoutSeconds = 180,
    # The network profile of tools/load/network-profiles between the client and the worker, or
    # none for loopback.
    [string]$Network = "none",
    # How many times the network part leaves the World and comes back.
    [int]$Leaves = 3
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runUnityTests = Join-Path $PSScriptRoot "Run-UnityTests.ps1"
$logRoot = Join-Path ([IO.Path]::GetTempPath()) ("shooter-mmo-live-stack\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$gameServiceUrl = "http://localhost:5000"
$gameServicePort = 5000
$shardId = "local-shard-1"
$udpPort = 27015
# The Unity test checks that its join tickets name the worker this script started.
$runtimeVariable = "MMORPG_LIVE_STACK_WORKER_RUNTIME_ID"
# The proxy's port, which the worker advertises under a profile.
$proxyPort = 27115
$profilePath = Join-Path $repositoryRoot "tools\load\network-profiles\$Network.json"
$boundsPath = Join-Path $repositoryRoot "tools\load\network-client-bounds.json"
$proxyDll = Join-Path $repositoryRoot "tools\NetworkImpairmentProxy\bin\Debug\net10.0\Mmorpg.Tools.NetworkImpairmentProxy.dll"
# The variables the network part of the test reads.
$networkVariables = @("MMORPG_LIVE_STACK_NETWORK", "MMORPG_LIVE_STACK_WALK_SECONDS", "MMORPG_LIVE_STACK_LEAVES", "MMORPG_LIVE_STACK_RESULTS", "MMORPG_LIVE_STACK_BOUNDS", "MMORPG_LIVE_STACK_DIRECT_UDP_PORT")
$gameServiceDll = Join-Path $repositoryRoot "src\Server\GameService\bin\Debug\net10.0\Mmorpg.GameService.dll"
$workerDll = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Debug\net10.0\Mmorpg.SimulationWorker.dll"

$StackStepPrefix = "live"
. (Join-Path $PSScriptRoot "LocalStackCommon.ps1")

function Start-ServiceProcess([string]$Name, [string]$Dll, [hashtable]$Environment) {
    Start-StackProcess $Name "dotnet" @("`"$Dll`"") $Environment $logRoot $repositoryRoot
}

function Get-WorkerLog {
    Read-SharedText (Join-Path $logRoot "simulation-worker.log")
}

function Wait-Ready([scriptblock]$Condition, [string]$What, $Process, [string]$LogName) {
    Wait-Until $Condition $What $Process (Join-Path $logRoot $LogName) $ReadyTimeoutSeconds $logRoot
}

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
Write-Step "Logs: $logRoot"
$total = [Diagnostics.Stopwatch]::StartNew()
$exitCode = 2
$gameService = $null
$worker = $null
$proxy = $null
$containersToStop = @()
Push-Location $repositoryRoot
try {
    # Everything that can refuse the run is checked before anything starts.
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ".env"))) {
        throw "The repository has no .env file; create it from .env.example as docs/LOCAL_DEVELOPMENT.md describes."
    }

    $useProxy = $Network -ne "none"
    $walkSeconds = 30
    if ($useProxy) {
        if (-not (Test-Path -LiteralPath $profilePath)) {
            $known = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tools\load\network-profiles") -Filter "*.json" | ForEach-Object { $_.BaseName }
            throw "The network profile '$Network' does not exist. Profiles: none, $($known -join ', ')."
        }

        # A walk under a profile with outages lasts until the first outage is over, so the
        # measurements see the correction after it.
        $profileData = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
        $outages = $profileData.serverToClient.outages
        if ($outages) {
            $walkSeconds = [Math]::Max($walkSeconds, [int]([double]$outages.firstAfterSeconds + [double]$outages.durationSeconds + 15))
        }
    }

    & $runUnityTests -Preflight | ForEach-Object { Write-Step $_ }
    $listener = Get-TcpListener $gameServicePort
    if ($listener) {
        throw "Process $($listener.OwningProcess) already listens on port $gameServicePort. Stop the GameService that runs, then run the live test again."
    }

    $udpListener = Get-UdpListener $udpPort
    if ($udpListener) {
        throw "Process $($udpListener.OwningProcess) already uses UDP port $udpPort. Stop the SimulationWorker that runs, then run the live test again."
    }

    if ($useProxy) {
        $proxyListener = Get-UdpListener $proxyPort
        if ($proxyListener) {
            throw "Process $($proxyListener.OwningProcess) already uses UDP port $proxyPort, which the network impairment proxy needs. Stop it, then run the live test again."
        }
    }

    $composeServices = Get-NativeLines "docker" @("compose", "config", "--services")
    if ($composeServices.ExitCode -ne 0) {
        throw "Docker Compose cannot read docker-compose.yml; is Docker running?"
    }

    $runningServices = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
    if ($runningServices.ExitCode -ne 0) {
        throw "Docker Compose cannot list the running containers; is Docker running?"
    }

    # Recorded before the start, so an interrupted start still stops what it started.
    $containersToStop = @($composeServices.Lines | Where-Object { $runningServices.Lines -notcontains $_ })
    $stage = [Diagnostics.Stopwatch]::StartNew()
    if ($containersToStop.Count -gt 0) {
        Write-Step "Starting the containers $($containersToStop -join ', ')."
    }
    else {
        Write-Step "The containers run already and stay running."
    }

    if ((Invoke-Logged "docker" @("compose", "up", "-d", "--wait") (Join-Path $logRoot "docker-compose-up.log")) -ne 0) {
        Write-LogTail (Join-Path $logRoot "docker-compose-up.log")
        throw "docker compose up failed."
    }

    $projects = @("src\Server\GameService\Mmorpg.GameService.csproj", "src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj")
    if ($useProxy) {
        $projects += "tools\NetworkImpairmentProxy\Mmorpg.Tools.NetworkImpairmentProxy.csproj"
    }

    Write-Step "Building $(if ($useProxy) { 'GameService, SimulationWorker, and the network impairment proxy' } else { 'GameService and SimulationWorker' })."
    foreach ($project in $projects) {
        $logName = "build-" + [IO.Path]::GetFileNameWithoutExtension($project) + ".log"
        if ((Invoke-Logged "dotnet" @("build", $project, "-nologo", "-v", "q") (Join-Path $logRoot $logName)) -ne 0) {
            Write-LogTail (Join-Path $logRoot $logName)
            throw "Building $project failed."
        }
    }

    Write-Step "Starting GameService."
    $gameService = Start-ServiceProcess "game-service" $gameServiceDll @{
        ASPNETCORE_ENVIRONMENT = "Development"
        ASPNETCORE_URLS = $gameServiceUrl
    }
    Write-Step "GameService runs as process $($gameService.Id)."
    Wait-Ready { Test-HttpReady "$gameServiceUrl/health/ready" } "GameService readiness" $gameService "game-service.log"

    $workerEnvironment = @{ DOTNET_ENVIRONMENT = "Development" }
    if ($useProxy) {
        Write-Step "Starting the network impairment proxy with the profile $Network on UDP $proxyPort."
        $proxy = Start-StackProcess "network-proxy" "dotnet" @("`"$proxyDll`"", "--profile", "`"$profilePath`"", "--listen", "127.0.0.1:$proxyPort", "--forward", "127.0.0.1:$udpPort", "--counts", "`"$(Join-Path $logRoot 'network-proxy.json')`"") @{} $logRoot $repositoryRoot
        Wait-Ready { (Read-SharedText (Join-Path $logRoot "network-proxy.log")) -match "listening on UDP" } "The network impairment proxy" $proxy "network-proxy.log"
        # The worker keeps its own port and sends every client to the proxy.
        $workerEnvironment["SIMULATION_WORKER_ADVERTISED_UDP_PORT"] = "$proxyPort"
    }

    Write-Step "Starting SimulationWorker."
    $worker = Start-ServiceProcess "simulation-worker" $workerDll $workerEnvironment
    Write-Step "SimulationWorker runs as process $($worker.Id)."
    Wait-Ready { (Get-WorkerLog) -match "Realtime transport listening on UDP port $udpPort" } "The worker's UDP listener" $worker "simulation-worker.log"
    Wait-Ready { Test-ShardOnline $gameServiceUrl $shardId } "The registration of $shardId" $worker "simulation-worker.log"
    $runtimeMatch = [regex]::Match((Get-WorkerLog), "with runtime ([^,\s]+),")
    if (-not $runtimeMatch.Success) {
        throw "The worker log names no runtime id; the logs are in '$logRoot'."
    }

    $runtimeId = $runtimeMatch.Groups[1].Value
    Write-Step ("The stack is ready in {0:N0} s: GameService at $gameServiceUrl, worker runtime $runtimeId on UDP $udpPort." -f $stage.Elapsed.TotalSeconds)

    $stage = [Diagnostics.Stopwatch]::StartNew()
    $category = if ($useProxy) { "LiveStackNetwork" } else { "LiveStack" }
    Write-Step "Running the $category tests headless under the network profile $Network, with a walk of $walkSeconds s and $Leaves leaves."
    [Environment]::SetEnvironmentVariable($runtimeVariable, $runtimeId)
    [Environment]::SetEnvironmentVariable("MMORPG_LIVE_STACK_NETWORK", $Network)
    [Environment]::SetEnvironmentVariable("MMORPG_LIVE_STACK_WALK_SECONDS", "$walkSeconds")
    [Environment]::SetEnvironmentVariable("MMORPG_LIVE_STACK_LEAVES", "$Leaves")
    [Environment]::SetEnvironmentVariable("MMORPG_LIVE_STACK_RESULTS", $logRoot)
    [Environment]::SetEnvironmentVariable("MMORPG_LIVE_STACK_BOUNDS", $boundsPath)
    # The other player joins on the worker's own port, past the proxy.
    [Environment]::SetEnvironmentVariable("MMORPG_LIVE_STACK_DIRECT_UDP_PORT", "$udpPort")
    $exitCode = 1
    try {
        & $runUnityTests -Platform PlayMode -TestCategory $category -ResultsPath $logRoot | ForEach-Object { Write-Step $_ }
        $exitCode = 0
    }
    catch {
        Write-Step "The LiveStack test failed: $($_.Exception.Message)"
    }
    finally {
        [Environment]::SetEnvironmentVariable($runtimeVariable, $null)
        foreach ($name in $networkVariables) {
            [Environment]::SetEnvironmentVariable($name, $null)
        }
    }

    $unityLog = Join-Path $logRoot "PlayMode-unity.log"
    if (Test-Path -LiteralPath $unityLog) {
        Select-String -LiteralPath $unityLog -Pattern "^\[LIVE\] " | ForEach-Object { Write-Host "  $($_.Line.Substring(7))" }
    }

    Write-Step ("Unity ran the test in {0:N0} s." -f $stage.Elapsed.TotalSeconds)
}
catch {
    Write-Step "The live stack could not run the test: $($_.Exception.Message)"
    $exitCode = 2
}
finally {
    Stop-StackProcess $worker "SimulationWorker"
    Stop-StackProcess $gameService "GameService"
    # The proxy stops last and writes its counts.
    Stop-StackProcess $proxy "NetworkImpairmentProxy"
    $toStop = @()
    if ($containersToStop.Count -gt 0) {
        $nowRunning = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
        $toStop = @($containersToStop | Where-Object { $nowRunning.Lines -contains $_ })
    }

    if ($toStop.Count -gt 0) {
        $null = Invoke-Logged "docker" (@("compose", "stop") + $toStop) (Join-Path $logRoot "docker-compose-stop.log")
        Write-Step "Stopped the containers $($toStop -join ', ')."
    }

    Pop-Location
}

$proxyCounts = Join-Path $logRoot "network-proxy.json"
if (Test-Path -LiteralPath $proxyCounts) {
    Get-Content -LiteralPath (Join-Path $logRoot "network-proxy.log") | Select-Object -Last 7 | ForEach-Object { Write-Host "  $_" }
}

$result = if ($exitCode -eq 0) { "passed" } elseif ($exitCode -eq 1) { "FAILED" } else { "could not run" }
Write-Step ("Live stack test under the network profile $Network $result in {0:N0} s. Logs: $logRoot" -f $total.Elapsed.TotalSeconds)
exit $exitCode

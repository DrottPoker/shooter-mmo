# Runs the live stack test: the LiveStack PlayMode test of the Unity client, which joins a real
# SimulationWorker through the client's real network layer, against a local stack that this
# script starts. It starts PostgreSQL and Redis with Docker Compose when they do not run, builds
# GameService and SimulationWorker and starts them in the Development environment as background
# processes, waits until GameService is ready and the worker listens and has registered its
# Shard, and runs only the LiveStack tests headless through tools/Run-UnityTests.ps1.
#
# It always stops the two services, with Ctrl+C so the worker saves its characters and goes
# offline, and the containers it started, also when the test fails or the script is interrupted
# with Ctrl+C. It never touches containers or processes it did not start, and it refuses to run
# while GameService or a worker already uses its ports or a Unity Editor has the project open.
# The logs of a run stay in a folder below the system temp directory, which the script prints.
#
# Exit codes: 0 the scenario passed, 1 the test failed, 2 the stack or Unity could not run it.
param(
    # How long GameService and the worker may take to become ready.
    [int]$ReadyTimeoutSeconds = 180
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
$gameServiceDll = Join-Path $repositoryRoot "src\Server\GameService\bin\Debug\net10.0\Mmorpg.GameService.dll"
$workerDll = Join-Path $repositoryRoot "src\Server\SimulationWorker\bin\Debug\net10.0\Mmorpg.SimulationWorker.dll"

function Write-Step([string]$Message) {
    Write-Host "[live] $Message"
}

# Runs a native command with its output in a log file and returns its exit code. Windows
# PowerShell 5.1 turns redirected native stderr into errors under "Stop", so errors stay
# suspended while it runs and the exit code decides.
function Invoke-Logged([string]$FilePath, [string[]]$ArgumentList, [string]$LogName) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $FilePath @ArgumentList *> (Join-Path $logRoot $LogName)
        $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Get-NativeLines([string]$FilePath, [string[]]$ArgumentList) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = @(& $FilePath @ArgumentList 2>$null)
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($lines | Where-Object { $_ } | ForEach-Object { "$_".Trim() }) }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Write-LogTail([string]$LogName) {
    $path = Join-Path $logRoot $LogName
    if (Test-Path -LiteralPath $path) {
        Get-Content -LiteralPath $path -Tail 30 | ForEach-Object { Write-Host "  $_" }
    }
}

# Starts a built service in its own hidden console, so it can get Ctrl+C on its own, with its
# output in the log folder and the environment of its launch profile.
function Start-StackProcess([string]$Name, [string]$Dll, [hashtable]$Environment) {
    $previous = @{}
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }

    try {
        Start-Process -FilePath "dotnet" -ArgumentList "`"$Dll`"" -WorkingDirectory $repositoryRoot `
            -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $logRoot "$Name.log") `
            -RedirectStandardError (Join-Path $logRoot "$Name.err.log")
    }
    finally {
        foreach ($key in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key])
        }
    }
}

# Sends Ctrl+C to the console of a process that Start-StackProcess started. A helper process
# attaches to that console, because attaching detaches a process from its own console.
function Send-ControlC([int]$ProcessId) {
    $helper = @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleControl
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
'@
    $command = "Add-Type -TypeDefinition @'`n$helper`n'@`n" +
        "[void][ConsoleControl]::FreeConsole()`n" +
        "if (-not [ConsoleControl]::AttachConsole($ProcessId)) { exit 1 }`n" +
        "[void][ConsoleControl]::SetConsoleCtrlHandler([IntPtr]::Zero, `$true)`n" +
        "if (-not [ConsoleControl]::GenerateConsoleCtrlEvent(0, 0)) { exit 2 }`n" +
        "exit 0"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $sender = Start-Process -FilePath "powershell.exe" `
        -ArgumentList "-NoProfile", "-NonInteractive", "-EncodedCommand", $encoded `
        -WindowStyle Hidden -Wait -PassThru
    $sender.ExitCode -eq 0
}

# Stops a service with Ctrl+C, as its terminal would, and ends it when it does not stop in time.
function Stop-StackProcess($Process, [string]$Name) {
    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    $signalled = Send-ControlC $Process.Id
    if ($signalled -and $Process.WaitForExit(30000)) {
        Write-Step "$Name stopped."
        return
    }

    Write-Step "$Name did not stop on Ctrl+C within 30 seconds; ending the process."
    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    $null = $Process.WaitForExit(10000)
}

function Test-GameServiceReady {
    try {
        $response = Invoke-WebRequest -Uri "$gameServiceUrl/health/ready" -UseBasicParsing -TimeoutSec 2
        $response.StatusCode -eq 200
    }
    catch {
        $false
    }
}

function Test-ShardOnline {
    try {
        $shards = @(Invoke-RestMethod -Uri "$gameServiceUrl/api/shards" -TimeoutSec 2)
        @($shards | Where-Object { $_.id -eq $shardId -and $_.isOnline }).Count -eq 1
    }
    catch {
        $false
    }
}

function Wait-Until([scriptblock]$Condition, [string]$What, $Process, [string]$LogName) {
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    while (-not (& $Condition)) {
        if ($Process.HasExited) {
            Write-LogTail $LogName
            throw "$What failed: the process ended; its log is in '$logRoot'."
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            Write-LogTail $LogName
            throw "$What did not happen within $ReadyTimeoutSeconds seconds; the logs are in '$logRoot'."
        }

        Start-Sleep -Milliseconds 500
    }
}

function Get-WorkerLog {
    $path = Join-Path $logRoot "simulation-worker.log"
    if (Test-Path -LiteralPath $path) {
        # The worker still writes the file, so it is read without a lock.
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            (New-Object IO.StreamReader($stream)).ReadToEnd()
        }
        finally {
            $stream.Dispose()
        }
    }
    else {
        ""
    }
}

New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
Write-Step "Logs: $logRoot"
$total = [Diagnostics.Stopwatch]::StartNew()
$exitCode = 2
$gameService = $null
$worker = $null
$containersToStop = @()
Push-Location $repositoryRoot
try {
    # Everything that can refuse the run is checked before anything starts.
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot ".env"))) {
        throw "The repository has no .env file; create it from .env.example as docs/LOCAL_DEVELOPMENT.md describes."
    }

    & $runUnityTests -Preflight | ForEach-Object { Write-Step $_ }
    $listener = Get-NetTCPConnection -LocalPort $gameServicePort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        throw "Process $($listener.OwningProcess) already listens on port $gameServicePort. Stop the GameService that runs, then run the live test again."
    }

    $udpListener = Get-NetUDPEndpoint -LocalPort $udpPort -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($udpListener) {
        throw "Process $($udpListener.OwningProcess) already uses UDP port $udpPort. Stop the SimulationWorker that runs, then run the live test again."
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

    if ((Invoke-Logged "docker" @("compose", "up", "-d", "--wait") "docker-compose-up.log") -ne 0) {
        Write-LogTail "docker-compose-up.log"
        throw "docker compose up failed."
    }

    Write-Step "Building GameService and SimulationWorker."
    foreach ($project in @("src\Server\GameService\Mmorpg.GameService.csproj", "src\Server\SimulationWorker\Mmorpg.SimulationWorker.csproj")) {
        $logName = "build-" + [IO.Path]::GetFileNameWithoutExtension($project) + ".log"
        if ((Invoke-Logged "dotnet" @("build", $project, "-nologo", "-v", "q") $logName) -ne 0) {
            Write-LogTail $logName
            throw "Building $project failed."
        }
    }

    Write-Step "Starting GameService."
    $gameService = Start-StackProcess "game-service" $gameServiceDll @{
        ASPNETCORE_ENVIRONMENT = "Development"
        ASPNETCORE_URLS = $gameServiceUrl
    }
    Write-Step "GameService runs as process $($gameService.Id)."
    Wait-Until { Test-GameServiceReady } "GameService readiness" $gameService "game-service.log"

    Write-Step "Starting SimulationWorker."
    $worker = Start-StackProcess "simulation-worker" $workerDll @{ DOTNET_ENVIRONMENT = "Development" }
    Write-Step "SimulationWorker runs as process $($worker.Id)."
    Wait-Until { (Get-WorkerLog) -match "Realtime transport listening on UDP port $udpPort" } "The worker's UDP listener" $worker "simulation-worker.log"
    Wait-Until { Test-ShardOnline } "The registration of $shardId" $worker "simulation-worker.log"
    $runtimeMatch = [regex]::Match((Get-WorkerLog), "with runtime ([^,\s]+),")
    if (-not $runtimeMatch.Success) {
        throw "The worker log names no runtime id; the logs are in '$logRoot'."
    }

    $runtimeId = $runtimeMatch.Groups[1].Value
    Write-Step ("The stack is ready in {0:N0} s: GameService at $gameServiceUrl, worker runtime $runtimeId on UDP $udpPort." -f $stage.Elapsed.TotalSeconds)

    $stage = [Diagnostics.Stopwatch]::StartNew()
    Write-Step "Running the LiveStack test headless."
    [Environment]::SetEnvironmentVariable($runtimeVariable, $runtimeId)
    $exitCode = 1
    try {
        & $runUnityTests -Platform PlayMode -TestCategory "LiveStack" -ResultsPath $logRoot | ForEach-Object { Write-Step $_ }
        $exitCode = 0
    }
    catch {
        Write-Step "The LiveStack test failed: $($_.Exception.Message)"
    }
    finally {
        [Environment]::SetEnvironmentVariable($runtimeVariable, $null)
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
    $toStop = @()
    if ($containersToStop.Count -gt 0) {
        $nowRunning = Get-NativeLines "docker" @("compose", "ps", "--status", "running", "--services")
        $toStop = @($containersToStop | Where-Object { $nowRunning.Lines -contains $_ })
    }

    if ($toStop.Count -gt 0) {
        $null = Invoke-Logged "docker" (@("compose", "stop") + $toStop) "docker-compose-stop.log"
        Write-Step "Stopped the containers $($toStop -join ', ')."
    }

    Pop-Location
}

$result = if ($exitCode -eq 0) { "passed" } elseif ($exitCode -eq 1) { "FAILED" } else { "could not run" }
Write-Step ("Live stack test $result in {0:N0} s. Logs: $logRoot" -f $total.Elapsed.TotalSeconds)
exit $exitCode

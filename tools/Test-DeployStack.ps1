# The smoke test of the production-like stack. It starts the stack (tools/Start-DeployStack.ps1),
# plays through it as a player does with no Development switch, then stops it in order and checks
# the stop. Exit code 0 only when every check held.
#
# The player path is tools/DeploySmokeTest, built on the bot client library. Through the TLS
# proxy, trusting the proxy's own certificate authority explicitly, it registers accounts, logs in,
# creates characters, lists the Shards, joins, moves for half a minute over UDP, leaves, and logs
# out; it checks /health/ready, that a forged X-Forwarded-For is limited as the real client, that
# the worker's routes refuse a wrong secret, and that the Development routes are absent. Then the
# `audit` command of the GameService image must find the economy of what the players did
# consistent. A player who stays in the world through the stop sees what the stop does:
#
#   1. The worker stops while GameService answers: it writes its last checkpoint and goes offline,
#      and the player is told server_shutdown.
#   2. GameService stops while the proxy is up: readiness turns to 503 stopping first and the
#      proxy takes it out of rotation, then it stops within its grace period.
#   3. Everything else stops, every container ended with exit code 0, and no service logged an
#      error.
#
# -KeepRunning runs the player path only and leaves the stack running, for a look by hand or a
# client. -RemoveVolumes removes the stack's volumes when it ends, as CI does. -NoBuild uses the
# images that are built. -Telemetry adds the export to the monitoring stack, which must run
# first. -WalkSeconds is how long the players move. The logs of every service and of the test
# go to -ResultsDirectory, below artifacts/deploy-smoke by default. docs/LOCAL_DEVELOPMENT.md
# describes it.
param(
    [switch]$NoBuild,
    [switch]$KeepRunning,
    [switch]$RemoveVolumes,
    [switch]$Telemetry,
    [int]$WalkSeconds = 30,
    [string]$ResultsDirectory = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DeployStackCommon.ps1")

$services = @("postgres", "redis", "migrate", "game-service", "simulation-worker", "proxy")
$dotnetServices = @("migrate", "game-service", "simulation-worker")
$workerId = "deploy-worker-1"

if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $script:DeployRoot ("artifacts/deploy-smoke/" + (Get-Date).ToString("yyyyMMdd-HHmmss"))
}

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

$results = New-Object System.Collections.Generic.List[object]
$background = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$Name, [bool]$Passed, [string]$Detail) {
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $Passed })
    $state = if ($Passed) { "[ok]    " } else { "[FAILED]" }
    Write-Host "$state $Name`: $Detail"
}

# Runs a native command with its output on the console, and returns its exit code. Windows
# PowerShell 5.1 turns native stderr into errors under "Stop", so errors stay suspended.
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

# Reads a file that a running process still writes, without locking it.
function Read-LogText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        (New-Object IO.StreamReader($stream)).ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

# Starts the smoke test tool in the background with its output in a log file.
function Start-SmokeProcess([string]$Command, [string]$Name, [string[]]$ExtraArguments) {
    $log = Join-Path $ResultsDirectory "$Name.log"
    $errorLog = Join-Path $ResultsDirectory "$Name.err.log"
    $arguments = @("`"$smokeDll`"", $Command, "--url", $url, "--ca", "`"$ca`"", "--expected-endpoint", $workerEndpoint) + $ExtraArguments
    $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -RedirectStandardOutput $log `
        -RedirectStandardError $errorLog -PassThru -NoNewWindow
    # Reading the handle keeps the exit code available after the process ended.
    $null = $process.Handle
    $entry = [pscustomobject]@{ Name = $Name; Process = $process; Log = $log }
    $background.Add($entry)
    $entry
}

# Waits until the log of a background process holds the marker, or the process ends, or the time is up.
function Wait-Marker($Entry, [string]$Marker, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Read-LogText $Entry.Log).Contains($Marker)) {
            return $true
        }

        if ($Entry.Process.HasExited) {
            return (Read-LogText $Entry.Log).Contains($Marker)
        }

        Start-Sleep -Milliseconds 250
    }

    $false
}

function Save-ServiceLogs {
    foreach ($service in $services) {
        $result = Get-DeployComposeLines @("logs", "--no-color", "--no-log-prefix", $service) -Telemetry:$Telemetry
        [IO.File]::WriteAllText((Join-Path $ResultsDirectory "$service.log"), (($result.Lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
    }
}

function Get-ServiceLog([string]$Service) {
    Read-LogText (Join-Path $ResultsDirectory "$Service.log")
}

# The lines of a log that mean an error: the console logger's fail and crit levels for the .NET
# services; the error level of Caddy's own log, without its access log, which records every 5xx
# answer, and without the one error that the stop provokes on purpose, the proxy's own answer
# "no upstreams available" to the watcher after it took GameService out of rotation; and
# PostgreSQL's ERROR, FATAL, and PANIC.
function Get-ErrorLines([string]$Service) {
    $lines = @((Get-ServiceLog $Service) -split "`n" | Where-Object { $_ })
    if ($dotnetServices -contains $Service) {
        return @($lines | Where-Object { $_ -match '^(fail|crit):' })
    }

    if ($Service -eq "proxy") {
        return @($lines | Where-Object {
                $entry = $null
                try { $entry = $_ | ConvertFrom-Json } catch { }
                $entry -and $entry.level -in @("error", "fatal", "panic") -and "$($entry.logger)" -notlike "http.log.access*" -and
                -not ("$($entry.logger)" -like "http.log.error*" -and $entry.msg -eq "no upstreams available")
            })
    }

    if ($Service -eq "postgres") {
        return @($lines | Where-Object { $_ -match '\b(ERROR|FATAL|PANIC):' })
    }

    @()
}

$smokeProject = Join-Path $script:DeployRoot "tools/DeploySmokeTest/Mmorpg.Tools.DeploySmokeTest.csproj"
$smokeDll = Join-Path $script:DeployRoot "tools/DeploySmokeTest/bin/Release/net10.0/Mmorpg.Tools.DeploySmokeTest.dll"
$endpoints = Get-DeployEndpoints
$url = $endpoints.Url
$workerEndpoint = $endpoints.Worker
$ca = Get-DeployCaPath
$stackStarted = $false

try {
    if (-not (Test-DeployDocker)) {
        throw "Docker Compose is not available. Start Docker Desktop, or install the Compose plugin."
    }

    Write-DeployStep "Building the smoke test tool."
    if ((Invoke-Native "dotnet" @("restore", $smokeProject, "--locked-mode")) -ne 0 -or
        (Invoke-Native "dotnet" @("build", $smokeProject, "--configuration", "Release", "--no-restore")) -ne 0) {
        throw "The smoke test tool did not build."
    }

    $startArguments = @{ NoBuild = [bool]$NoBuild; Telemetry = [bool]$Telemetry }
    & (Join-Path $PSScriptRoot "Start-DeployStack.ps1") @startArguments
    $stackStarted = $true
    if ($LASTEXITCODE -ne 0) {
        throw "The stack did not start."
    }

    if (-not (Test-Path -LiteralPath $ca)) {
        throw "The proxy's root certificate is missing at $ca."
    }

    # A player who stays in the world through the stop, joined before the player path begins.
    $hold = $null
    if (-not $KeepRunning) {
        Write-DeployStep "A player joins and stays in the world."
        $hold = Start-SmokeProcess "hold" "hold" @("--timeout-seconds", "900")
        $holding = Wait-Marker $hold "HOLDING" 90
        Add-Result "player in the world" $holding "the hold player joined through the proxy and the worker"
    }

    Write-DeployStep "The player path."
    $playLines = New-Object System.Collections.Generic.List[string]
    $playArguments = @($smokeDll, "play", "--url", $url, "--ca", $ca, "--expected-endpoint", $workerEndpoint, "--walk-seconds", "$WalkSeconds", "--worker-id", $workerId)
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & dotnet @playArguments | ForEach-Object { $playLines.Add("$_"); Write-Host $_ }
        $playExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
        [IO.File]::WriteAllText((Join-Path $ResultsDirectory "play.log"), (($playLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
    }

    Add-Result "player path" ($playExit -eq 0) "tools/DeploySmokeTest play exited with code $playExit"

    # The economy after the play: the audit command of the GameService image, as an operator runs
    # it, reads every item, slot, stack, and ledger row of the database in one snapshot and must
    # find them consistent. The players are still in the world, and the audit only reads.
    Write-DeployStep "The economy audit."
    $audit = Get-DeployComposeLines @("run", "--rm", "-T", "--no-deps", "migrate", "audit") -Telemetry:$Telemetry
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory "audit.log"), (($audit.Lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
    $audit.Lines | ForEach-Object { Write-Host "  $_" }
    $consistent = (($audit.Lines -join "`n").Contains("The economy is consistent."))
    Add-Result "economy audit" ($audit.ExitCode -eq 0 -and $consistent) "audit through the GameService image exited with code $($audit.ExitCode)$(if ($consistent) { ' and found the economy consistent' } else { ' without finding the economy consistent' })"

    if (-not $KeepRunning) {
        Write-DeployStep "The orderly stop."
        $holdAlive = $hold -and -not $hold.Process.HasExited
        Add-Result "player still in the world" $holdAlive "the hold player is still connected before the stop"

        # 1. The worker, while GameService still answers.
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $workerStop = Invoke-DeployCompose @("stop", "simulation-worker") -Telemetry:$Telemetry
        Add-Result "worker stop" ($workerStop -eq 0) ("docker compose stop took {0:N1} s" -f $watch.Elapsed.TotalSeconds)
        if ($hold) {
            $null = $hold.Process.WaitForExit(30000)
            $holdLog = Read-LogText $hold.Log
            Add-Result "player told of the stop" ($hold.Process.HasExited -and $hold.Process.ExitCode -eq 0 -and $holdLog.Contains("ENDED server_shutdown")) "the hold player ended with the worker's server_shutdown"
        }

        # 2. GameService, while the proxy is up and a watcher asks it for readiness.
        $watcher = Start-SmokeProcess "watch-ready" "watch-ready" @("--timeout-seconds", "120")
        $watching = Wait-Marker $watcher "WATCHING" 30
        Add-Result "readiness watched" $watching "the watcher saw GameService ready through the proxy"
        $watch.Restart()
        $gameServiceStop = Invoke-DeployCompose @("stop", "game-service") -Telemetry:$Telemetry
        Add-Result "game service stop" ($gameServiceStop -eq 0 -and $watch.Elapsed.TotalSeconds -lt 45) ("docker compose stop took {0:N1} s, inside its 45 s grace period" -f $watch.Elapsed.TotalSeconds)
        $null = $watcher.Process.WaitForExit(60000)
        $watchLog = Read-LogText $watcher.Log
        $unready = [regex]::Match($watchLog, "unready first: ([^\r\n]*)")
        $rotation = [regex]::Match($watchLog, "out of rotation after it was unready: ([^\r\n]*)")
        Add-Result "game service unready first" ($watcher.Process.HasExited -and $watcher.Process.ExitCode -eq 0) ("$($unready.Groups[1].Value); $($rotation.Groups[1].Value)")

        # 3. The rest, in Compose's order.
        $restStop = Invoke-DeployCompose @("stop") -Telemetry:$Telemetry
        Add-Result "stack stop" ($restStop -eq 0) "docker compose stop ended every remaining container"
    }
}
catch {
    Add-Result "run" $false $_.Exception.Message
}
finally {
    foreach ($entry in $background) {
        if (-not $entry.Process.HasExited) {
            Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    if ($stackStarted) {
        Save-ServiceLogs
    }
}

if ($stackStarted -and -not $KeepRunning) {
    # What the logs and the exit codes say about the stop.
    $workerLog = Get-ServiceLog "simulation-worker"
    $checkpoint = [regex]::Match($workerLog, "Saved the place and vitals of (\d+) of (\d+) players at shutdown")
    Add-Result "worker checkpoint" ($checkpoint.Success -and $checkpoint.Groups[1].Value -eq $checkpoint.Groups[2].Value -and [int]$checkpoint.Groups[2].Value -gt 0) (
        $(if ($checkpoint.Success) { "the worker saved $($checkpoint.Groups[1].Value) of $($checkpoint.Groups[2].Value) players in its last checkpoint" } else { "the worker logged no last checkpoint" }))
    Add-Result "worker offline" $workerLog.Contains("was marked offline") "the worker told GameService that it went offline"
    Add-Result "game service drain" (Get-ServiceLog "game-service").Contains("GameService is stopping: readiness reports stopping") "GameService logged that readiness turned to stopping and that it kept serving"

    foreach ($service in $services) {
        $errorLines = @(Get-ErrorLines $service)
        Add-Result "no errors in $service" ($errorLines.Count -eq 0) $(if ($errorLines.Count -eq 0) { "no error in its log" } else { "$($errorLines.Count) error lines, the first: $($errorLines[0])" })
    }

    $containers = @(Get-DeployContainers -Telemetry:$Telemetry)
    $notClean = @($containers | Where-Object { $_.State -ne "exited" -or $_.ExitCode -ne 0 })
    Add-Result "clean exit codes" ($containers.Count -eq $services.Count -and $notClean.Count -eq 0) $(if ($notClean.Count -eq 0) { "every one of $($containers.Count) containers ended with exit code 0" } else { "not clean: " + (($notClean | ForEach-Object { "$($_.Service) $($_.State) $($_.ExitCode)" }) -join ", ") })
}

if (-not $KeepRunning) {
    # Also after a start that failed, so no container of the stack is left behind.
    $removal = @{ RemoveVolumes = [bool]$RemoveVolumes }
    & (Join-Path $PSScriptRoot "Stop-DeployStack.ps1") @removal | Out-Host
}
else {
    Write-DeployStep "The stack keeps running: $url, and tools/Stop-DeployStack.ps1 stops it."
}

Write-Host ""
$failed = @($results | Where-Object { -not $_.Passed })
Write-Host "Logs: $ResultsDirectory"
if ($failed.Count -gt 0) {
    Write-Host "Result: $($failed.Count) of $($results.Count) checks failed: $(($failed | ForEach-Object { $_.Name }) -join ', ')"
    exit 1
}

Write-Host "Result: every one of $($results.Count) checks passed"
exit 0

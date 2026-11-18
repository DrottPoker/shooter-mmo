# Shared helpers of the production-like stack scripts, tools/New-DeploySecrets.ps1,
# tools/Start-DeployStack.ps1, tools/Stop-DeployStack.ps1, and tools/Test-DeployStack.ps1. They
# work in Windows PowerShell 5.1 and in PowerShell 7. Dot-source it from the script that needs it.
#
# Every Docker call of the stack names its Compose project and its Compose files, so nothing
# can reach a container, an image, a network, or a volume that is not this stack's.

$script:DeployRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:DeployProject = "shooter-mmo-deploy"
$script:DeployComposeFile = Join-Path $script:DeployRoot "deploy/docker-compose.production-like.yml"
$script:DeployTelemetryFile = Join-Path $script:DeployRoot "deploy/docker-compose.telemetry.yml"
$script:DeployEnvFile = Join-Path $script:DeployRoot "deploy/stack.env"
$script:DeployPostgresVolume = "$($script:DeployProject)_postgres_data"

# The services that run until they are stopped, and the one-shot job that must finish first.
$script:DeployLongRunningServices = @("postgres", "redis", "game-service", "simulation-worker", "proxy")

function Write-DeployStep([string]$Message) {
    Write-Host "[deploy] $Message"
}

function Test-DeployDocker {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & docker compose version *> $null
        $LASTEXITCODE -eq 0
    }
    catch {
        $false
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

# The text of the ports that the stack publishes, with the defaults of the Compose file and the
# overrides of the environment.
function Get-DeployEndpoints {
    $bind = if ($env:STACK_BIND_ADDRESS) { $env:STACK_BIND_ADDRESS } else { "127.0.0.1" }
    $https = if ($env:PROXY_HTTPS_PORT) { $env:PROXY_HTTPS_PORT } else { "8443" }
    $udp = if ($env:WORKER_UDP_PORT) { $env:WORKER_UDP_PORT } else { "28015" }
    $advertised = if ($env:WORKER_ADVERTISED_HOST) { $env:WORKER_ADVERTISED_HOST } else { "127.0.0.1" }
    [pscustomobject]@{
        Bind = $bind
        HttpsPort = [int]$https
        UdpPort = [int]$udp
        Url = "https://localhost:$https"
        Worker = "${advertised}:$udp"
    }
}

# The arguments that start every Docker Compose call of the stack, with the telemetry overlay when
# asked. Without the environment file, which only a stack that never started lacks, the calls that
# need no secret, such as stop and down, still work: the required variables get a placeholder
# value in this process only.
function Get-DeployComposeArguments([switch]$Telemetry) {
    $arguments = @("compose", "-p", $script:DeployProject, "-f", $script:DeployComposeFile)
    if ($Telemetry) {
        $arguments += @("-f", $script:DeployTelemetryFile)
    }

    if (Test-Path -LiteralPath $script:DeployEnvFile) {
        $arguments += @("--env-file", $script:DeployEnvFile)
    }
    else {
        foreach ($name in @("POSTGRES_PASSWORD", "REDIS_PASSWORD", "WORKER_1_SECRET")) {
            if (-not [Environment]::GetEnvironmentVariable($name)) {
                [Environment]::SetEnvironmentVariable($name, "not-a-secret-the-stack-never-started")
            }
        }
    }

    $arguments
}

# Runs docker with the stack's Compose arguments and returns its exit code; its output goes to the
# console. Windows PowerShell 5.1 turns native stderr into errors under "Stop", so errors stay
# suspended while it runs and the exit code decides.
function Invoke-DeployCompose([string[]]$Arguments, [switch]$Telemetry) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $all = @(Get-DeployComposeArguments -Telemetry:$Telemetry) + $Arguments
        & docker @all | Out-Host
        $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

# Runs docker with the stack's Compose arguments and returns its exit code and its standard output
# lines; the error output goes to the console.
function Get-DeployComposeLines([string[]]$Arguments, [switch]$Telemetry) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $all = @(Get-DeployComposeArguments -Telemetry:$Telemetry) + $Arguments
        $lines = @(& docker @all)
        $exitCode = $LASTEXITCODE
        [pscustomobject]@{ ExitCode = $exitCode; Lines = @($lines | ForEach-Object { "$_" }) }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

# Runs docker with other arguments and returns its exit code and output lines.
function Get-DeployDockerLines([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lines = @(& docker @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        [pscustomobject]@{ ExitCode = $exitCode; Lines = @($lines | ForEach-Object { "$_" }) }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

# A random secret of hexadecimal digits, which no connection string or header treats specially.
function New-DeployRandomSecret([int]$ByteCount = 24) {
    $bytes = New-Object byte[] $ByteCount
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

# Writes the environment file with three new random secrets. It never prints them.
function New-DeployEnvFile {
    $lines = @(
        "# Made by tools/New-DeploySecrets.ps1 for the production-like stack. Git ignores this file.",
        "# The PostgreSQL password lives inside its volume once the stack ran, so changing it here",
        "# alone does not change it: remove the volumes first with tools/Stop-DeployStack.ps1 -RemoveVolumes.",
        "POSTGRES_PASSWORD=$(New-DeployRandomSecret)",
        "REDIS_PASSWORD=$(New-DeployRandomSecret)",
        "# The secret of the worker deploy-worker-1: GameService holds it, and the worker presents it.",
        "WORKER_1_SECRET=$(New-DeployRandomSecret)"
    )
    $content = ($lines -join "`n") + "`n"
    [IO.File]::WriteAllText($script:DeployEnvFile, $content, (New-Object Text.UTF8Encoding $false))
    # Only the owner reads it where the platform has such permissions.
    if (Get-Command chmod -ErrorAction SilentlyContinue) {
        & chmod 600 $script:DeployEnvFile
    }
}

# The state of every container of the stack: Service, State, Health (empty without a health
# check), and ExitCode. Compose prints one JSON object per line, or an array in older versions.
function Get-DeployContainers([switch]$Telemetry) {
    $result = Get-DeployComposeLines @("ps", "-a", "--format", "json") -Telemetry:$Telemetry
    if ($result.ExitCode -ne 0) {
        return @()
    }

    $containers = New-Object System.Collections.Generic.List[object]
    foreach ($line in $result.Lines) {
        $text = $line.Trim()
        if (-not $text) { continue }
        $parsed = $text | ConvertFrom-Json
        if ($parsed -is [System.Array]) {
            foreach ($item in $parsed) { $containers.Add($item) }
        }
        else {
            $containers.Add($parsed)
        }
    }

    $containers.ToArray()
}

# Waits until every long-running service is healthy and the migration job ended with exit code 0.
# Returns $true, or $false after printing why not.
function Wait-DeployStackHealthy([int]$TimeoutSeconds = 300, [switch]$Telemetry) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $nextReport = Get-Date
    while ((Get-Date) -lt $deadline) {
        $containers = @(Get-DeployContainers -Telemetry:$Telemetry)
        $problems = @()
        $waiting = @()
        foreach ($service in $script:DeployLongRunningServices) {
            $container = $containers | Where-Object { $_.Service -eq $service } | Select-Object -First 1
            if (-not $container) { $waiting += "$service (not created)"; continue }
            if ($container.State -in @("exited", "dead")) { $problems += "$service $($container.State) with exit code $($container.ExitCode)"; continue }
            if ($container.Health -ne "healthy") { $waiting += "$service ($($container.State), $($container.Health))" }
        }

        $migrate = $containers | Where-Object { $_.Service -eq "migrate" } | Select-Object -First 1
        if ($migrate -and $migrate.State -eq "exited" -and $migrate.ExitCode -ne 0) {
            $problems += "migrate exited with code $($migrate.ExitCode)"
        }

        if ($problems.Count -gt 0) {
            Write-DeployStep ("Not healthy: " + ($problems -join "; "))
            return $false
        }

        if ($waiting.Count -eq 0) {
            return $true
        }

        if ((Get-Date) -ge $nextReport) {
            Write-DeployStep ("Waiting for " + ($waiting -join ", "))
            $nextReport = (Get-Date).AddSeconds(10)
        }

        Start-Sleep -Seconds 2
    }

    Write-DeployStep "The stack was not healthy within $TimeoutSeconds seconds."
    $false
}

# The last lines of one service's log, for a failure report. Never the secrets: the services log
# none, and the lines are only read here.
function Write-DeployLogTail([string]$Service, [int]$Lines = 40, [switch]$Telemetry) {
    $result = Get-DeployComposeLines @("logs", "--no-color", "--no-log-prefix", "--tail", "$Lines", $Service) -Telemetry:$Telemetry
    Write-Host "---- $Service (last $Lines lines)"
    $result.Lines | ForEach-Object { Write-Host "  $_" }
}

# Whether a TCP or UDP port can be bound on an address, so a stack that cannot publish its port
# says so before Docker does with a less helpful message.
function Test-DeployPortFree([string]$Address, [int]$Port, [string]$Protocol) {
    $ip = [Net.IPAddress]::Parse($Address)
    try {
        if ($Protocol -eq "udp") {
            $socket = New-Object Net.Sockets.UdpClient((New-Object Net.IPEndPoint($ip, $Port)))
            $socket.Close()
        }
        else {
            $listener = New-Object Net.Sockets.TcpListener($ip, $Port)
            $listener.Start()
            $listener.Stop()
        }

        $true
    }
    catch [Net.Sockets.SocketException] {
        $false
    }
}

# The folder where the stack's root certificate goes for clients, below artifacts, which Git ignores.
function Get-DeployCaPath {
    Join-Path $script:DeployRoot "artifacts/deploy-stack/root-ca.crt"
}

# Copies the root certificate of the proxy's own certificate authority out of the proxy's data
# volume, which is what a client trusts to reach the stack over TLS. Returns the path, or $null.
function Export-DeployCaCertificate([switch]$Telemetry) {
    $path = Get-DeployCaPath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $result = Get-DeployComposeLines @("cp", "proxy:/data/caddy/pki/authorities/local/root.crt", $path) -Telemetry:$Telemetry
    if ($result.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $path)) {
        return $null
    }

    $path
}

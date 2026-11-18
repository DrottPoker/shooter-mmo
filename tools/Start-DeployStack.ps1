# Starts the production-like stack of deploy/docker-compose.production-like.yml: GameService and a
# SimulationWorker in the Production environment behind a TLS proxy, with PostgreSQL and Redis. It
# makes deploy/stack.env with random secrets when the file is missing, builds the two images,
# starts the containers, waits until every service is healthy, and prints where the stack
# listens. The stack is never a deployment: it runs on this machine only, and nothing here
# assumes a real host.
#
# -NoBuild uses the images that are already built. -Telemetry adds the overlay that exports to the
# monitoring stack, which must run first. -TimeoutSeconds is how long the start may take to be
# healthy. Run tools/Stop-DeployStack.ps1 to stop it. docs/LOCAL_DEVELOPMENT.md describes it.
param(
    [switch]$NoBuild,
    [switch]$Telemetry,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DeployStackCommon.ps1")

if (-not (Test-DeployDocker)) {
    Write-DeployStep "Docker Compose is not available. Start Docker Desktop, or install the Compose plugin."
    exit 2
}

if (-not (Test-Path -LiteralPath $script:DeployEnvFile)) {
    New-DeployEnvFile
    Write-DeployStep "Made deploy/stack.env with random secrets. Git ignores it, and nothing prints it."
}

# A port that another program holds stops the start with Docker's own message, so say it first. A
# stack that already runs holds its own ports.
$endpoints = Get-DeployEndpoints
$running = @(Get-DeployContainers -Telemetry:$Telemetry | Where-Object { $_.State -eq "running" })
if ($running.Count -eq 0) {
    if (-not (Test-DeployPortFree $endpoints.Bind $endpoints.HttpsPort "tcp")) {
        Write-DeployStep "TCP port $($endpoints.HttpsPort) on $($endpoints.Bind) is in use. Set PROXY_HTTPS_PORT to another port."
        exit 1
    }

    if (-not (Test-DeployPortFree $endpoints.Bind $endpoints.UdpPort "udp")) {
        Write-DeployStep "UDP port $($endpoints.UdpPort) on $($endpoints.Bind) is in use. Set WORKER_UDP_PORT to another port."
        exit 1
    }
}

$watch = [Diagnostics.Stopwatch]::StartNew()
if (-not $NoBuild) {
    Write-DeployStep "Building the GameService and SimulationWorker images."
    if ((Invoke-DeployCompose @("build") -Telemetry:$Telemetry) -ne 0) {
        Write-DeployStep "The image build failed."
        exit 1
    }

    Write-DeployStep ("Built in {0:N0} s." -f $watch.Elapsed.TotalSeconds)
}

Write-DeployStep "Starting the stack."
$upArguments = @("up", "-d", "--no-build", "--wait", "--wait-timeout", "$TimeoutSeconds")
if ((Invoke-DeployCompose $upArguments -Telemetry:$Telemetry) -ne 0) {
    Write-DeployStep "The stack did not become healthy."
    foreach ($container in @(Get-DeployContainers -Telemetry:$Telemetry)) {
        Write-DeployStep (("  $($container.Service): $($container.State) $($container.Health)").TrimEnd())
        if ($container.State -ne "running" -or $container.Health -eq "unhealthy") {
            Write-DeployLogTail $container.Service 30 -Telemetry:$Telemetry
        }
    }

    exit 1
}

if (-not (Wait-DeployStackHealthy -TimeoutSeconds 60 -Telemetry:$Telemetry)) {
    exit 1
}

$ca = Export-DeployCaCertificate -Telemetry:$Telemetry
Write-DeployStep ("The stack is healthy after {0:N0} s." -f $watch.Elapsed.TotalSeconds)
Write-Host ""
Write-Host "GameService, through the TLS proxy:  $($endpoints.Url)"
Write-Host "Realtime, UDP:                       $($endpoints.Worker) (the address that the worker advertises)"
if ($ca) {
    Write-Host "Certificate authority to trust:      $ca"
}
else {
    Write-Host "The certificate authority could not be copied out of the proxy; see docs/DEPLOYMENT.md."
}

Write-Host "PostgreSQL and Redis publish no port: they are reachable inside the stack only."
Write-Host "Smoke test:  tools/Test-DeployStack.ps1     Stop:  tools/Stop-DeployStack.ps1"
exit 0

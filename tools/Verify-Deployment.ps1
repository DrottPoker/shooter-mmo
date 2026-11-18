# Checks the deploy files of the production-like stack without starting it: deploy/Dockerfile,
# .dockerignore, deploy/docker-compose.production-like.yml with its telemetry overlay, and the
# generator of its secrets. Needs Docker Compose to render the Compose file; nothing is built,
# started, or left behind. CI and tools/Test-Changes.ps1 run it. docs/DEPLOYMENT.md describes the
# rules.
#
# What it holds the files to:
#   - every image tag is pinned to a version, so Dependabot has something to update;
#   - no secret has a default: the Compose file does not render without each of the three;
#   - PostgreSQL and Redis publish no port, and every published port binds a chosen address;
#   - every service has a health check and a restart policy, a read-only root file system, GameService
#     and the worker a grace period at least 5 seconds longer than their shutdown timeout, which is
#     30 seconds for GameService and 40 for the worker unless the Compose file sets it, and the
#     services that run
#     .NET or the proxy drop every capability;
#   - Git ignores the secrets file, and the build context holds no client, art, or tests;
#   - the generator makes distinct random secrets of enough length and prints none.
$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $PSScriptRoot "DeployStackCommon.ps1")

$problems = New-Object System.Collections.Generic.List[string]
function Add-Problem([string]$Message) {
    $problems.Add($Message)
    Write-Host "  $Message"
}

# Docker prints its own errors, which these checks expect for the cases that must fail.
function Invoke-Compose([string[]]$Arguments, [hashtable]$Environment = @{}) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $saved = @{}
    foreach ($name in $Environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $Environment[$name])
    }

    try {
        $lines = @(& docker compose -p shooter-mmo-deploy-verify @Arguments 2>&1)
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($lines | ForEach-Object { "$_" }) }
    }
    finally {
        foreach ($name in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name])
        }

        $ErrorActionPreference = $previousPreference
    }
}

function Invoke-GitCheckIgnore([string]$Path) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & git -C $repositoryRoot check-ignore -q $Path 2>$null
        $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

if (-not (Test-DeployDocker)) {
    Write-Host "Docker Compose is not available, and the Compose file cannot be rendered without it."
    exit 2
}

$composeFile = $script:DeployComposeFile
$telemetryFile = $script:DeployTelemetryFile
$dummy = @{
    POSTGRES_PASSWORD = "verify-only-postgres-value"
    REDIS_PASSWORD = "verify-only-redis-value"
    WORKER_1_SECRET = "verify-only-worker-value"
}

# The variables of the host must not decide what is rendered: the settings with defaults are unset.
$environment = @{}
foreach ($name in $dummy.Keys) { $environment[$name] = $dummy[$name] }
foreach ($name in @("STACK_BIND_ADDRESS", "PROXY_HTTPS_PORT", "WORKER_UDP_PORT", "WORKER_ADVERTISED_HOST", "EDGE_SUBNET")) { $environment[$name] = $null }

Write-Host "== Compose file"
$render = Invoke-Compose @("-f", $composeFile, "config", "--format", "json") $environment
if ($render.ExitCode -ne 0) {
    Add-Problem "The Compose file does not render: $($render.Lines -join ' ')"
}
else {
    $config = ($render.Lines -join "`n") | ConvertFrom-Json

    $overlay = Invoke-Compose @("-f", $composeFile, "-f", $telemetryFile, "config", "-q") $environment
    if ($overlay.ExitCode -ne 0) {
        Add-Problem "The telemetry overlay does not render with the Compose file: $($overlay.Lines -join ' ')"
    }

    # No secret has a default: without any one of them, rendering stops.
    foreach ($secret in $dummy.Keys) {
        $without = @{}
        foreach ($name in $environment.Keys) { $without[$name] = $environment[$name] }
        $without[$secret] = $null
        if ((Invoke-Compose @("-f", $composeFile, "config", "-q") $without).ExitCode -eq 0) {
            Add-Problem "The Compose file renders without $secret, so the secret has a default."
        }
    }

    $names = @($config.services.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($service in @("postgres", "redis", "migrate", "game-service", "simulation-worker", "proxy")) {
        if ($names -notcontains $service) { Add-Problem "The Compose file has no service $service." }
    }

    foreach ($name in $names) {
        $service = $config.services.$name
        $longRunning = $name -ne "migrate"
        $ports = @($service.ports | Where-Object { $_ })
        if ($name -in @("postgres", "redis", "migrate", "game-service") -and $ports.Count -gt 0) {
            Add-Problem "$name publishes a port; it is reachable inside the stack only."
        }

        foreach ($port in $ports) {
            if ($port.host_ip -ne "127.0.0.1") {
                Add-Problem "$name publishes $($port.published) on '$($port.host_ip)', not on 127.0.0.1 by default."
            }
        }

        if ($longRunning -and -not $service.healthcheck) { Add-Problem "$name has no health check." }
        if ($longRunning -and $service.restart -notin @("unless-stopped", "always")) { Add-Problem "$name has no restart policy." }
        if (-not $longRunning -and $service.restart -ne "no") { Add-Problem "$name is a one-shot job and must not restart." }
        if ($service.image -match ':latest$' -or ($service.image -notmatch ':' -and -not $service.build)) {
            Add-Problem "$name uses the image '$($service.image)' without a pinned version."
        }

        if (-not $service.build -and $service.image -notmatch ':\d+\.\d+') {
            Add-Problem "$name pins no version of '$($service.image)'; use a tag such as 16.16-alpine."
        }

        if ($service.read_only -ne $true) { Add-Problem "$name has a writable root file system." }
        if ($name -in @("game-service", "simulation-worker", "migrate", "proxy") -and ($service.cap_drop -notcontains "ALL")) {
            Add-Problem "$name does not drop its capabilities."
        }

        # The host's shutdown timeout, from the setting that the Compose file gives it or else the
        # default of the service, which docs/SERVICE_FEATURES.md lists. The runtime must wait for the
        # whole orderly stop and then some, or it kills the process while it still saves its players.
        $shutdowns = @{
            "game-service"      = @{ Key = "Http__ShutdownTimeoutSeconds"; Default = 30 }
            "simulation-worker" = @{ Key = "SimulationWorker__ShutdownTimeoutSeconds"; Default = 40 }
        }
        if ($shutdowns.ContainsKey($name)) {
            $grace = [string]$service.stop_grace_period
            # Compose renders a duration as 45s, 1m0s, or 2m30s.
            $seconds = if ($grace -match '^(?:(\d+)m)?(?:(\d+)s)?$' -and $grace) {
                (60 * [int]"0$($Matches[1])") + [int]"0$($Matches[2])"
            } else { 0 }
            $timeout = $shutdowns[$name].Default
            $configured = $service.environment.PSObject.Properties[$shutdowns[$name].Key]
            if ($configured -and "$($configured.Value)" -match '^\d+$') { $timeout = [int]"$($configured.Value)" }
            if ($seconds -lt $timeout + 5) {
                Add-Problem "$name waits $grace for its stop; the host's shutdown timeout is $timeout s, so the grace period must be at least 5 s longer."
            }
        }
    }

    # What GameService and its job need outside Development, from the rendered environment.
    foreach ($name in @("game-service", "migrate")) {
        $environmentOfService = $config.services.$name.environment
        if ($environmentOfService.DOTNET_ENVIRONMENT -ne "Production") { Add-Problem "$name does not run in the Production environment." }
        foreach ($required in @("AllowedHosts", "Http__TrustedProxies", "ConnectionStrings__Postgres", "ConnectionStrings__Redis")) {
            if (-not $environmentOfService.$required) { Add-Problem "$name sets no $required." }
        }

        if ("$($environmentOfService.AllowedHosts)" -match '\*|0\.0\.0\.0|\[::\]') { Add-Problem "$name allows any host." }
        if ("$($environmentOfService.ConnectionStrings__Postgres)" -notlike "*Password=$($dummy.POSTGRES_PASSWORD)*") { Add-Problem "$name's PostgreSQL password does not come from POSTGRES_PASSWORD." }
        if ("$($environmentOfService.ConnectionStrings__Redis)" -notlike "*password=$($dummy.REDIS_PASSWORD)") { Add-Problem "$name's Redis password does not come from REDIS_PASSWORD." }
        if ($environmentOfService.PSObject.Properties.Name -contains "SIMULATION_WORKER_SERVICE_SECRET") { Add-Problem "$name sets the shared worker secret, which only Development accepts." }
    }

    $worker = $config.services.'simulation-worker'.environment
    if ($worker.DOTNET_ENVIRONMENT -ne "Production") { Add-Problem "simulation-worker does not run in the Production environment." }
    if ($worker.SimulationWorker__GameServiceSecret -ne $dummy.WORKER_1_SECRET) { Add-Problem "The worker's secret does not come from WORKER_1_SECRET." }
    $held = $config.services.'game-service'.environment."ServiceAuthentication__SimulationWorkers__$($worker.SimulationWorker__SimulationWorkerId)"
    if ($held -ne $dummy.WORKER_1_SECRET) { Add-Problem "GameService holds no secret for the worker id $($worker.SimulationWorker__SimulationWorkerId)." }
    if (-not $worker.SimulationWorker__HealthFilePath) { Add-Problem "The worker has no health file for its health check." }
    if ($worker.SimulationWorker__AdvertisedHost -in @("0.0.0.0", "::", "")) { Add-Problem "The worker advertises a wildcard address." }

    # The trusted proxies are exactly the edge network, which only the proxy and GameService join.
    $edge = $config.networks.edge
    $trusted = $config.services.'game-service'.environment.Http__TrustedProxies
    if ($edge.ipam.config[0].subnet -ne $trusted) { Add-Problem "Http:TrustedProxies is '$trusted', not the edge network $($edge.ipam.config[0].subnet)." }
    $edgeMembers = @($names | Where-Object { $config.services.$_.networks.PSObject.Properties.Name -contains "edge" })
    if (($edgeMembers | Sort-Object) -join "," -ne "game-service,proxy") { Add-Problem "The edge network holds $($edgeMembers -join ', '), not only the proxy and GameService." }
    if ($edge.internal -ne $true -or $config.networks.backend.internal -ne $true) { Add-Problem "The edge and backend networks must be internal." }
}

Write-Host "== Dockerfile"
$dockerfile = Get-Content -LiteralPath (Join-Path $repositoryRoot "deploy/Dockerfile")
$stages = @($dockerfile | ForEach-Object { if ($_ -match '^\s*FROM\s+\S+\s+AS\s+(\S+)') { $Matches[1] } })
foreach ($line in $dockerfile) {
    if ($line -match '^\s*FROM\s+(\S+)') {
        $image = $Matches[1]
        if ($stages -contains $image) { continue }
        if ($image -notmatch ':\d+\.\d+' -or $image -match ':latest') {
            Add-Problem "The Dockerfile uses '$image' without a pinned version."
        }
    }
}

foreach ($target in @("game-service", "simulation-worker")) {
    if (-not ($dockerfile | Where-Object { $_ -match "^\s*FROM\s+\S+\s+AS\s+$target\s*$" })) { Add-Problem "The Dockerfile has no target $target." }
}

if (@($dockerfile | Where-Object { $_ -match '^\s*USER\s+' }).Count -lt 2) { Add-Problem "Both runtime stages must set USER, so no service runs as root." }
if (@($dockerfile | Where-Object { $_ -match 'dotnet restore .*--locked-mode' }).Count -eq 0) { Add-Problem "The Dockerfile must restore with --locked-mode." }

Write-Host "== Build context"
$ignore = @(Get-Content -LiteralPath (Join-Path $repositoryRoot ".dockerignore") | Where-Object { $_ -and -not $_.StartsWith("#") })
if ($ignore[0] -ne "**") { Add-Problem ".dockerignore must start by ignoring everything (**) and then allow what the images build." }
foreach ($unwanted in @("src/Client", "art", "artifacts", "tools", "tests", "docs")) {
    if ($ignore | Where-Object { $_.StartsWith("!$unwanted") }) { Add-Problem ".dockerignore lets $unwanted into the build context." }
}

Write-Host "== Secrets"
$ignored = Invoke-GitCheckIgnore "deploy/stack.env"
if (-not $ignored) { Add-Problem "Git does not ignore deploy/stack.env." }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("verify-deploy-" + [Guid]::NewGuid().ToString("N") + ".env")
try {
    $script:DeployEnvFile = $temporary
    $output = @(New-DeployEnvFile)
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($temporary)) {
        if ($line -match '^([A-Z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
    }

    foreach ($name in @("POSTGRES_PASSWORD", "REDIS_PASSWORD", "WORKER_1_SECRET")) {
        if (-not $values.ContainsKey($name)) { Add-Problem "The secret generator does not write $name."; continue }
        if ($values[$name].Length -lt 32) { Add-Problem "The generated $name is shorter than 32 characters." }
        if ($values[$name] -notmatch '^[0-9a-f]+$') { Add-Problem "The generated $name is not plain hexadecimal, which connection strings need." }
        if (($output -join "`n").Contains($values[$name])) { Add-Problem "The secret generator printed $name." }
    }

    if (@($values.Values | Select-Object -Unique).Count -ne $values.Count) { Add-Problem "The generated secrets are not distinct." }

    # The scripts speak of the endpoints that Compose really uses: the shell first, then the file.
    $savedPort = $env:PROXY_HTTPS_PORT
    $savedUdp = $env:WORKER_UDP_PORT
    try {
        $env:PROXY_HTTPS_PORT = $null
        $env:WORKER_UDP_PORT = $null
        [IO.File]::AppendAllText($temporary, "PROXY_HTTPS_PORT=9443`n# WORKER_UDP_PORT=1`nWORKER_UDP_PORT = `"29015`"`n")
        $fromFile = Get-DeployEndpoints
        if ($fromFile.HttpsPort -ne 9443 -or $fromFile.Url -ne "https://localhost:9443") { Add-Problem "The endpoints ignore PROXY_HTTPS_PORT of the environment file." }
        if ($fromFile.UdpPort -ne 29015) { Add-Problem "The endpoints ignore WORKER_UDP_PORT of the environment file." }
        $env:PROXY_HTTPS_PORT = "9555"
        if ((Get-DeployEndpoints).HttpsPort -ne 9555) { Add-Problem "The shell does not override the environment file for PROXY_HTTPS_PORT, as it does in Compose." }
    }
    finally {
        $env:PROXY_HTTPS_PORT = $savedPort
        $env:WORKER_UDP_PORT = $savedUdp
    }
}
finally {
    Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
}

# Without the environment file a call gets stand-in secrets for that call only, and they are
# placeholders that the services refuse.
$saved = @{}
foreach ($name in $script:DeploySecretNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $script:DeployEnvFile = Join-Path ([IO.Path]::GetTempPath()) ("verify-deploy-missing-" + [Guid]::NewGuid().ToString("N") + ".env")
    foreach ($name in $script:DeploySecretNames) { [Environment]::SetEnvironmentVariable($name, $null) }
    $env:REDIS_PASSWORD = "set-by-the-caller"
    $during = Invoke-WithDeployEnvironment { "$($env:POSTGRES_PASSWORD)|$($env:REDIS_PASSWORD)|$($env:WORKER_1_SECRET)" }
    if ($during -ne "$($script:DeployStandInSecret)|set-by-the-caller|$($script:DeployStandInSecret)") { Add-Problem "A call without the environment file does not get the stand-in secrets, or loses a value the caller set." }
    if ($env:POSTGRES_PASSWORD -or $env:WORKER_1_SECRET) { Add-Problem "A call without the environment file leaves its stand-in secrets in the shell, where a later start would use them." }
    if ($env:REDIS_PASSWORD -ne "set-by-the-caller") { Add-Problem "A call without the environment file removes a secret that the caller set." }
    if (-not $script:DeployStandInSecret.StartsWith("replace-with-")) { Add-Problem "The stand-in secret is not a placeholder that the services refuse." }
}
finally {
    foreach ($name in $script:DeploySecretNames) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}

if ($problems.Count -gt 0) {
    Write-Host "Result: $($problems.Count) problems in the deploy files"
    exit 1
}

Write-Host "Result: the deploy files hold every rule"
exit 0

# Validates the monitoring stack in observability/ with the images pinned in
# docker-compose.observability.yml: the OpenTelemetry Collector configuration, the
# Prometheus configuration and alert rules, the alert rule unit tests, and the Grafana
# dashboard JSON. Needs Docker; nothing is started or left running.
$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$observability = Join-Path $repositoryRoot "observability"
$compose = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot "docker-compose.observability.yml")

function Get-PinnedImage([string]$repository) {
    $pattern = "image:\s*(" + [regex]::Escape($repository) + ":[^\s]+)"
    $match = [regex]::Match($compose, $pattern)
    if (-not $match.Success) {
        throw "docker-compose.observability.yml does not pin an image for $repository."
    }

    return $match.Groups[1].Value
}

function Invoke-Checked([string]$description, [string[]]$arguments) {
    & docker @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$description failed."
    }
}

$collectorImage = Get-PinnedImage "otel/opentelemetry-collector-contrib"
$prometheusImage = Get-PinnedImage "prom/prometheus"
$collectorConfig = Join-Path $observability "otel-collector"
$prometheusConfig = Join-Path $observability "prometheus"

Invoke-Checked "Collector configuration validation" @(
    "run", "--rm", "-v", "${collectorConfig}:/etc/otelcol:ro",
    $collectorImage, "validate", "--config=/etc/otelcol/config.yaml")
Invoke-Checked "Prometheus configuration and rule check" @(
    "run", "--rm", "-v", "${prometheusConfig}:/etc/prometheus:ro", "--entrypoint", "promtool",
    $prometheusImage, "check", "config", "/etc/prometheus/prometheus.yml")
Invoke-Checked "Alert rule unit tests" @(
    "run", "--rm", "-v", "${prometheusConfig}:/etc/prometheus:ro", "-w", "/etc/prometheus/tests",
    "--entrypoint", "promtool", $prometheusImage, "test", "rules", "playtest.rules.test.yml")

$dashboards = @(Get-ChildItem -LiteralPath (Join-Path $observability "grafana/dashboards") -Filter *.json)
if ($dashboards.Count -eq 0) {
    throw "No Grafana dashboards found."
}

foreach ($dashboard in $dashboards) {
    $model = Get-Content -Raw -LiteralPath $dashboard.FullName | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($model.uid) -or [string]::IsNullOrWhiteSpace($model.title)) {
        throw "Dashboard $($dashboard.Name) needs a uid and a title."
    }
}

Write-Output "Observability verification passed: collector, Prometheus, alert tests, and $($dashboards.Count) dashboards."

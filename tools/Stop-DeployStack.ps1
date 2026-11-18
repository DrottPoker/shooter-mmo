# Stops the production-like stack in order and removes its containers and its network. Compose
# stops what depends on another service first, so the proxy and the worker stop before GameService,
# which stops before PostgreSQL and Redis, each within its own grace period. The PostgreSQL data and
# the proxy's certificate authority stay in their volumes, so the next start continues where this
# one ended.
#
# -RemoveVolumes also deletes the volumes, which are every account and character of the stack and
# its certificate authority. Only this stack's Compose project is touched: never another
# project's containers, images, networks, or volumes. docs/LOCAL_DEVELOPMENT.md describes it.
param(
    [switch]$RemoveVolumes
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DeployStackCommon.ps1")

if (-not (Test-DeployDocker)) {
    Write-DeployStep "Docker Compose is not available, so there is nothing to stop."
    exit 0
}

$arguments = @("down", "--remove-orphans")
if ($RemoveVolumes) {
    $arguments += "--volumes"
    Write-DeployStep "Stopping the stack and removing its volumes."
}
else {
    Write-DeployStep "Stopping the stack. Its volumes stay."
}

if ((Invoke-DeployCompose $arguments) -ne 0) {
    Write-DeployStep "Docker Compose could not stop the stack."
    exit 1
}

Write-DeployStep "Stopped."
exit 0

# Makes deploy/stack.env, the environment file of the production-like stack, with random secrets
# for the PostgreSQL password, the Redis password, and the secret of the worker, when the file does
# not exist. Git ignores the file, the Compose file has no default for any of them, and nothing
# here prints a secret. An existing file is kept, because the PostgreSQL password lives inside its
# volume once the stack ran.
#
# -Force replaces the file with new secrets. It refuses while the stack's PostgreSQL volume exists,
# because that volume would keep the old password: run tools/Stop-DeployStack.ps1 -RemoveVolumes
# first.
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DeployStackCommon.ps1")

if ((Test-Path -LiteralPath $script:DeployEnvFile) -and -not $Force) {
    Write-DeployStep "deploy/stack.env exists and is kept."
    exit 0
}

if ($Force -and (Test-DeployDocker)) {
    $volume = Get-DeployDockerLines @("volume", "inspect", $script:DeployPostgresVolume)
    if ($volume.ExitCode -eq 0) {
        Write-DeployStep "The PostgreSQL volume $($script:DeployPostgresVolume) exists and keeps its old password. Run tools/Stop-DeployStack.ps1 -RemoveVolumes first."
        exit 1
    }
}

New-DeployEnvFile
Write-DeployStep "Made deploy/stack.env with three new random secrets."
exit 0

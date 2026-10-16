# Job-started hook for the self-hosted Unity runner. The runner runs it before every job and
# fails the job before its first step when it exits with a non-zero code. It accepts only
# jobs that people with write access to the repository can start: pushes, manual runs, and
# pull requests from a branch of the repository itself. A workflow condition cannot replace
# it, because a pull request from a fork runs its own copy of the workflow file.
#
# Install a copy outside the runner folder and point ACTIONS_RUNNER_HOOK_JOB_STARTED in the
# runner's .env file at it, as docs/LOCAL_DEVELOPMENT.md describes. The copy is what runs,
# so reinstall it after changing this file. It must run on Windows PowerShell 5.1.
param(
    [string]$Repository = "DrottPoker/shooter-mmo"
)

$ErrorActionPreference = "Stop"

function Deny-Job {
    param([string]$Reason)

    Write-Output "::error::This self-hosted runner refused the job: $Reason"
    exit 1
}

if ($env:GITHUB_REPOSITORY -ne $Repository) {
    Deny-Job "repository '$env:GITHUB_REPOSITORY' is not '$Repository'."
}

$eventName = $env:GITHUB_EVENT_NAME
switch ($eventName) {
    "push" { }
    "workflow_dispatch" { }
    "pull_request" {
        if ([string]::IsNullOrWhiteSpace($env:GITHUB_EVENT_PATH) -or
            -not (Test-Path -LiteralPath $env:GITHUB_EVENT_PATH)) {
            Deny-Job "the pull request event payload is missing."
        }

        $payload = Get-Content -Raw -LiteralPath $env:GITHUB_EVENT_PATH | ConvertFrom-Json
        $headRepository = $payload.pull_request.head.repo.full_name
        if ($headRepository -ne $Repository) {
            Deny-Job "the pull request comes from '$headRepository', not from a branch of '$Repository'."
        }
    }
    default {
        Deny-Job "event '$eventName' may not run on this runner."
    }
}

Write-Output "This self-hosted runner accepted the $eventName job for $env:GITHUB_REF."

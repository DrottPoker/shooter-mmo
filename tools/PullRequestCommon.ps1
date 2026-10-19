# Shared helpers for Wait-PullRequest.ps1 and Complete-PullRequest.ps1. Dot-source this file.
# Windows PowerShell 5.1 turns native stderr into terminating errors under
# $ErrorActionPreference = "Stop" once it is redirected, so native commands run with errors
# suspended: stdout is captured, stderr reaches the console unchanged, and the exit code decides.

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    foreach ($argument in $ArgumentList) {
        # Windows PowerShell 5.1 does not escape embedded double quotes for native commands.
        if ($argument.Contains('"')) {
            throw "Native command arguments must not contain double quotes: $argument"
        }
    }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $FilePath @ArgumentList)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-GitHubJson {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    $result = Invoke-NativeCommand -FilePath "gh" -ArgumentList $ArgumentList
    if ($result.ExitCode -ne 0) {
        throw "gh $($ArgumentList -join ' ') failed with exit code $($result.ExitCode)."
    }
    ($result.Output -join "`n") | ConvertFrom-Json
}

# Runs git with the given arguments and returns its stdout lines; throws when git fails.
function Invoke-Git {
    $result = Invoke-NativeCommand -FilePath "git" -ArgumentList $args
    if ($result.ExitCode -ne 0) {
        throw "git $($args -join ' ') failed with exit code $($result.ExitCode)."
    }
    $result.Output
}

# Runs git with the given arguments and returns whether it exited with code 0.
function Test-Git {
    $result = Invoke-NativeCommand -FilePath "git" -ArgumentList $args
    $result.ExitCode -eq 0
}

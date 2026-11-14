# Checks the helpers of tools/UnityCommon.ps1 that put a working tree back after a Unity run,
# in a temporary Git repository. They touch the developer's own files, so every case of what was
# there before the run is exercised: an unchanged file, a file with local changes, a file the
# developer deleted, an untracked file, and a file outside the folders Unity owns.
#
# Exit codes: 0 every case holds, 1 a case failed.
$ErrorActionPreference = "Stop"
. (Join-Path (Split-Path -Parent $PSScriptRoot) "UnityCommon.ps1")

$root = Join-Path ([IO.Path]::GetTempPath()) ("unity-common-test-" + [Guid]::NewGuid().ToString("N"))
$failures = @()

function Write-TestFile([string]$Path, [string]$Content) {
    $fullPath = Join-Path $root $Path
    $null = New-Item -ItemType Directory -Force (Split-Path -Parent $fullPath)
    [IO.File]::WriteAllText($fullPath, $Content)
}

function Read-TestFile([string]$Path) {
    $fullPath = Join-Path $root $Path
    if (Test-Path -LiteralPath $fullPath) {
        return [IO.File]::ReadAllText($fullPath)
    }

    return $null
}

function Assert-Content([string]$Case, [string]$Path, $Expected) {
    $actual = Read-TestFile $Path
    if ($actual -ne $Expected) {
        $shown = if ($null -eq $actual) { "no file" } else { "'$actual'" }
        $wanted = if ($null -eq $Expected) { "no file" } else { "'$Expected'" }
        $script:failures += "${Case}: $Path holds $shown, expected $wanted."
    }
}

function Invoke-Git([string[]]$Arguments) {
    & git -C $root @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

try {
    $null = New-Item -ItemType Directory -Force $root
    Invoke-Git @("init", "--quiet")
    Invoke-Git @("config", "user.email", "test@example.invalid")
    Invoke-Git @("config", "user.name", "Test")
    Invoke-Git @("config", "core.autocrlf", "false")

    $owned = "src/Client/ProjectSettings"
    Write-TestFile "$owned/unchanged.asset" "committed"
    Write-TestFile "$owned/edited.asset" "committed"
    Write-TestFile "$owned/deleted-untouched.asset" "committed"
    Write-TestFile "$owned/deleted-rewritten.asset" "committed"
    Write-TestFile "$owned/deleted-rewritten-differently.asset" "committed"
    Write-TestFile "$owned/staged-deletion.asset" "committed"
    Write-TestFile "src/Server/outside.txt" "committed"
    Invoke-Git @("add", "--all")
    Invoke-Git @("commit", "--quiet", "--message", "Start")

    # What the developer had done before the run.
    Write-TestFile "$owned/edited.asset" "edited by the developer"
    Remove-Item -LiteralPath (Join-Path $root "$owned/deleted-untouched.asset")
    Remove-Item -LiteralPath (Join-Path $root "$owned/deleted-rewritten.asset")
    Remove-Item -LiteralPath (Join-Path $root "$owned/deleted-rewritten-differently.asset")
    Invoke-Git @("rm", "--quiet", "$owned/staged-deletion.asset")
    Write-TestFile "$owned/untracked-before.asset" "the developer's new file"

    $before = Get-WorkingTreeState $root

    # What a Unity run does.
    Write-TestFile "$owned/unchanged.asset" "rewritten by Unity"
    Write-TestFile "$owned/edited.asset" "rewritten by Unity"
    Write-TestFile "$owned/deleted-rewritten.asset" "committed"
    Write-TestFile "$owned/deleted-rewritten-differently.asset" "rewritten by Unity"
    Write-TestFile "$owned/staged-deletion.asset" "rewritten by Unity"
    Write-TestFile "$owned/created-by-unity.asset" "new"
    Write-TestFile "src/Server/outside.txt" "changed outside the Unity folders"

    $restored = @(Restore-UnityOwnedFiles $before $root)

    Assert-Content "A file that matched the index" "$owned/unchanged.asset" "committed"
    Assert-Content "A file with local changes" "$owned/edited.asset" "edited by the developer"
    Assert-Content "A deleted file that the run left alone" "$owned/deleted-untouched.asset" $null
    Assert-Content "A deleted file that the run wrote again as committed" "$owned/deleted-rewritten.asset" $null
    Assert-Content "A deleted file that the run wrote again differently" "$owned/deleted-rewritten-differently.asset" $null
    Assert-Content "A staged deletion that the run wrote again" "$owned/staged-deletion.asset" $null
    Assert-Content "An untracked file from before the run" "$owned/untracked-before.asset" "the developer's new file"
    Assert-Content "A file that the run created" "$owned/created-by-unity.asset" $null
    Assert-Content "A file outside the Unity folders" "src/Server/outside.txt" "changed outside the Unity folders"

    $differences = @(Compare-WorkingTreeState $before $root)
    $unexpected = @($differences | Where-Object { $_ -notmatch 'src/Server/outside\.txt$' })
    if ($unexpected.Count -gt 0) {
        $failures += "The working tree differs from before the run: $($unexpected -join '; ')"
    }

    if ($restored.Count -ne 6) {
        $failures += "Expected six restore lines, got $($restored.Count): $($restored -join ' | ')"
    }

    # The player log is found by the company and product names of the player settings. The
    # Windows player writes it below the user's LocalLow folder, so the case runs on Windows only.
    if ($env:OS -eq "Windows_NT") {
        Write-TestFile "player-project/ProjectSettings/ProjectSettings.asset" "PlayerSettings:`n  companyName: Test Company`n  productName: Test Product`n  productGUID: 0`n"
        $expectedLog = Join-Path (Join-Path (Split-Path -Parent $env:LOCALAPPDATA) "LocalLow") "Test Company\Test Product\Player.log"
        $playerLog = Get-UnityPlayerLogPath (Join-Path $root "player-project")
        if ($playerLog -ne $expectedLog) {
            $failures += "The player log is '$playerLog', expected '$expectedLog'."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "  $failure"
    }

    Write-Host "Result: the Unity file restore helpers failed $($failures.Count) case(s)"
    exit 1
}

Write-Host "Result: the Unity file restore helpers hold in every case"
exit 0

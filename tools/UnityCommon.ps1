# Shared helpers of the scripts that run Unity headless on the client project: the pinned
# Editor, the project lock, process arguments, and the tracked files that a Unity run rewrites.
# Dot-source it after setting $ErrorActionPreference.

# The Editor version that ProjectVersion.txt pins.
function Get-UnityRequiredVersion([string]$ProjectPath) {
    return ((Get-Content (Join-Path $ProjectPath "ProjectSettings\ProjectVersion.txt")) |
        Where-Object { $_ -match '^m_EditorVersion: ' }) -replace '^m_EditorVersion: ', ''
}

# An explicit path, or the Unity Hub's default path of the pinned version on Windows.
function Get-UnityEditorPath([string]$ExplicitPath, [string]$Version) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return $ExplicitPath
    }

    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        return "C:\Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe"
    }

    return $null
}

# Unity holds Temp/UnityLockfile open without sharing while it has the project open, in the
# Editor and headless alike.
function Test-UnityProjectOpen([string]$Path) {
    $lockFile = Join-Path $Path "Temp\UnityLockfile"
    if (-not (Test-Path -LiteralPath $lockFile)) {
        return $false
    }

    try {
        $stream = [IO.File]::Open($lockFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Dispose()
        return $false
    }
    catch [IO.IOException] {
        return $true
    }
}

# The log of a player of the project: below LocalLow, in the folder of the company and product
# names of its player settings, which the test runner keeps for its test player. The next start
# of a player of the project replaces it.
function Get-UnityPlayerLogPath([string]$ProjectPath) {
    $settings = Get-Content -LiteralPath (Join-Path $ProjectPath "ProjectSettings\ProjectSettings.asset")
    $company = (($settings | Where-Object { $_ -match '^  companyName: ' }) -replace '^  companyName: ', '').Trim()
    $product = (($settings | Where-Object { $_ -match '^  productName: ' }) -replace '^  productName: ', '').Trim()
    $localLow = Join-Path (Split-Path -Parent $env:LOCALAPPDATA) "LocalLow"
    return Join-Path $localLow "$company\$product\Player.log"
}

function ConvertTo-ProcessArgument {
    param([string]$Value)

    if ($Value.Contains('"')) {
        throw "Process arguments cannot contain double quotes."
    }

    if ($Value -match '\s') {
        return '"' + $Value + '"'
    }

    return $Value
}

# Folders and files of the Unity project that Unity itself writes during a headless run, such
# as a hash in the Addressables settings, the player settings that the test runner sets while
# it builds a test player, the shader prefiltering data and runtime settings list that a player
# build writes into the render pipeline assets, and the link.xml that Addressables writes for
# the build. A run that leaves such a file changed is put back, and a new untracked file there is
# removed; a change anywhere else is never touched.
$UnityOwnedPrefixes = @(
    "src/Client/ProjectSettings/",
    "src/Client/Packages/",
    "src/Client/Assets/AddressableAssetsData/",
    "src/Client/Assets/Settings/",
    "src/Client/Assets/UniversalRenderPipelineGlobalSettings.asset"
)

function Test-UnityOwnedPath([string]$Path) {
    foreach ($prefix in $UnityOwnedPrefixes) {
        if ($Path.StartsWith($prefix, [StringComparison]::Ordinal)) {
            return $true
        }
    }

    return $false
}

function Get-GitLines([string]$RepositoryRoot, [string[]]$Arguments) {
    $output = & git -C $RepositoryRoot -c core.quotepath=off @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

# The state of the working tree before a Unity run: git status, the contents of the
# Unity-owned files that already differ from the index, and the Unity-owned files that the
# developer deleted without staging it, so a run that changes them further can be put back to
# exactly this.
function Get-WorkingTreeState([string]$RepositoryRoot) {
    $snapshots = @{}
    $deleted = @()
    foreach ($path in Get-GitLines $RepositoryRoot @("diff", "--name-only")) {
        if (Test-UnityOwnedPath $path) {
            $fullPath = Join-Path $RepositoryRoot $path
            if (Test-Path -LiteralPath $fullPath) {
                $snapshots[$path] = [IO.File]::ReadAllBytes($fullPath)
            }
            else {
                $deleted += $path
            }
        }
    }

    return [pscustomobject]@{
        Status = @(Get-GitLines $RepositoryRoot @("status", "--porcelain", "--untracked-files=all"))
        Untracked = @(Get-GitLines $RepositoryRoot @("ls-files", "--others", "--exclude-standard"))
        Snapshots = $snapshots
        Deleted = $deleted
    }
}

# Runs a file operation again while another process, such as a Unity process that is still
# shutting down, holds the file, for up to a minute.
function Invoke-FileOperation([scriptblock]$Operation, [string]$What) {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        try {
            & $Operation
            return
        }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ((Get-Date) -gt $deadline) {
                throw "$What failed for a minute: $($_.Exception.Message)"
            }

            Start-Sleep -Seconds 1
        }
    }
}

# Puts back every Unity-owned file that the run changed and returns one line per file it put
# back. A file that matched the index before is checked out from the index; one that had
# changes gets the content it had; one that the developer had deleted stays deleted, and is
# removed again when Unity wrote it anew; a new untracked file is removed.
function Restore-UnityOwnedFiles($Before, [string]$RepositoryRoot) {
    $restored = @()
    foreach ($path in Get-GitLines $RepositoryRoot @("ls-files", "--others", "--exclude-standard")) {
        if ((Test-UnityOwnedPath $path) -and $Before.Untracked -notcontains $path) {
            $fullPath = Join-Path $RepositoryRoot $path
            Invoke-FileOperation { [IO.File]::Delete($fullPath) } "Removing $path"
            $restored += "Removed $path, which Unity created during the run."
        }
    }

    $changed = @(Get-GitLines $RepositoryRoot @("diff", "--name-only")) + @($Before.Snapshots.Keys) + @($Before.Deleted)
    foreach ($path in ($changed | Sort-Object -Unique)) {
        if (-not (Test-UnityOwnedPath $path)) {
            continue
        }

        $fullPath = Join-Path $RepositoryRoot $path
        if ($Before.Deleted -contains $path) {
            # Never check a file out that was deleted before the run: that would undo the
            # developer's deletion.
            if (Test-Path -LiteralPath $fullPath) {
                Invoke-FileOperation { [IO.File]::Delete($fullPath) } "Removing $path"
                $restored += "Removed $path, which was deleted before the run and Unity wrote again."
            }
        }
        elseif ($Before.Snapshots.ContainsKey($path)) {
            $current = if (Test-Path -LiteralPath $fullPath) { [IO.File]::ReadAllBytes($fullPath) } else { $null }
            $previous = $Before.Snapshots[$path]
            if ($null -eq $current -or [Convert]::ToBase64String($current) -ne [Convert]::ToBase64String($previous)) {
                Invoke-FileOperation { [IO.File]::WriteAllBytes($fullPath, $previous) } "Restoring $path"
                $restored += "Restored $path, which Unity rewrote during the run."
            }
        }
        else {
            Invoke-FileOperation {
                & git -C $RepositoryRoot checkout -q -- $path
                if ($LASTEXITCODE -ne 0) {
                    throw [IO.IOException]::new("git checkout -- $path failed with exit code $LASTEXITCODE.")
                }
            } "Checking out $path"
            $restored += "Restored $path, which Unity rewrote during the run."
        }
    }

    return $restored
}

# The lines of git status that differ from the state before the run, as "+ line" for a new or
# changed entry and "- line" for one that is gone.
function Compare-WorkingTreeState($Before, [string]$RepositoryRoot) {
    $after = @(Get-GitLines $RepositoryRoot @("status", "--porcelain", "--untracked-files=all"))
    $differences = @()
    foreach ($line in $after) {
        if ($Before.Status -notcontains $line) {
            $differences += "+ $line"
        }
    }

    foreach ($line in $Before.Status) {
        if ($after -notcontains $line) {
            $differences += "- $line"
        }
    }

    return $differences
}

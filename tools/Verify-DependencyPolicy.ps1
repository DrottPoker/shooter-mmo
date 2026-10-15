$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$unityRoot = Join-Path $repositoryRoot "src/Client"
$manifestPath = Join-Path $unityRoot "Packages\manifest.json"
$unityLockPath = Join-Path $unityRoot "Packages\packages-lock.json"
$exactVersionPattern = '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
foreach ($dependency in $manifest.dependencies.PSObject.Properties) {
    $version = [string]$dependency.Value
    if ($version.StartsWith("file:")) {
        $localPath = Join-Path (Split-Path $manifestPath) $version.Substring(5)
        if (-not (Test-Path -LiteralPath $localPath)) {
            throw "Unity local package '$($dependency.Name)' points to missing path '$version'."
        }

        continue
    }

    if ($version -notmatch $exactVersionPattern) {
        throw "Unity dependency '$($dependency.Name)' must use an exact version, actual value: '$version'."
    }
}

if (-not (Test-Path -LiteralPath $unityLockPath)) {
    throw "Unity Packages/packages-lock.json is required."
}

$unityLock = Get-Content -Raw -LiteralPath $unityLockPath | ConvertFrom-Json
$lockedDependencyNames = $unityLock.dependencies.PSObject.Properties.Name
foreach ($dependency in $manifest.dependencies.PSObject.Properties) {
    if ($dependency.Name -notin $lockedDependencyNames) {
        throw "Unity lock file is missing direct dependency '$($dependency.Name)'."
    }
}

$liteNetLibVersions = @{}
$xunitV3Versions = @{}
# Shared props and targets files carry package references too. Build output and the agent
# worktrees below .claude are not repository sources.
$projectFiles = Get-ChildItem -Path $repositoryRoot -Recurse -Include *.csproj, *.props, *.targets -File |
    Where-Object {
        $relativePath = $_.FullName.Substring($repositoryRoot.Length)
        $relativePath -notmatch '[\\/](bin|obj)[\\/]' -and $relativePath -notmatch '^[\\/]\.claude[\\/]'
    }
foreach ($projectFile in $projectFiles) {
    $projectPath = $projectFile.FullName.Substring($repositoryRoot.Length + 1).Replace('\', '/')
    [xml]$project = Get-Content -Raw -LiteralPath $projectFile.FullName
    foreach ($packageReference in $project.Project.ItemGroup.PackageReference) {
        if ($null -eq $packageReference) {
            continue
        }

        $version = [string]$packageReference.Version
        if ([string]::IsNullOrWhiteSpace($version)) {
            $version = [string]$packageReference.Version.'#text'
        }

        if ($version -notmatch $exactVersionPattern) {
            throw "NuGet dependency '$($packageReference.Include)' in '$projectPath' must use an exact version."
        }

        if ($packageReference.Include -eq "LiteNetLib") {
            $liteNetLibVersions[$projectPath] = $version
        }

        if ($packageReference.Include -like "xunit.v3*") {
            $xunitV3Versions["$projectPath $($packageReference.Include)"] = $version
        }
    }
}

# The xunit.v3 packages ship together, so test projects and test libraries must use one version.
$distinctXunitV3Versions = @($xunitV3Versions.Values | Sort-Object -Unique)
if ($distinctXunitV3Versions.Count -gt 1) {
    $details = ($xunitV3Versions.GetEnumerator() | Sort-Object Name |
        ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ", "
    throw "xunit.v3 package versions must match across the test projects: $details."
}

# The server, bot tools, and Unity client must speak the same LiteNetLib wire protocol.
$liteNetLibVersions["Packages/manifest.json"] = [string]$manifest.dependencies.'com.revenantx.litenetlib'
$distinctLiteNetLibVersions = @($liteNetLibVersions.Values | Sort-Object -Unique)
if ($distinctLiteNetLibVersions.Count -ne 1) {
    $details = ($liteNetLibVersions.GetEnumerator() | Sort-Object Name |
        ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ", "
    throw "LiteNetLib versions must match across the server, tools, and Unity client: $details."
}

$trackedBuilds = & git -C $repositoryRoot ls-files "src/Client/ClientBuilds" "src/Client/Builds"
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect tracked Unity build output."
}

if ($trackedBuilds) {
    throw "Generated Unity client builds must not be tracked by Git."
}

Write-Output "Dependency policy verification passed."

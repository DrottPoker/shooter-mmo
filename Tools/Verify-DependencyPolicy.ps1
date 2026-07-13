$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$unityRoot = Join-Path $repositoryRoot "shooter-mmorpg-unity-client"
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

$projectFiles = Get-ChildItem -Path $repositoryRoot -Recurse -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($projectFile in $projectFiles) {
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
            throw "NuGet dependency '$($packageReference.Include)' in '$($projectFile.Name)' must use an exact version."
        }
    }
}

$trackedBuilds = & git -C $repositoryRoot ls-files "shooter-mmorpg-unity-client/ClientBuilds" "shooter-mmorpg-unity-client/Builds"
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect tracked Unity build output."
}

if ($trackedBuilds) {
    throw "Generated Unity client builds must not be tracked by Git."
}

Write-Output "Dependency policy verification passed."

# Builds or updates the .blend file of a paint layer from the bodies its asset.json names: one
# joined mesh per body in its rest pose, drawn with the paint over its skin, and the paint image
# T_Paint_<Name>_<Label> of every body. The first body's paint is the one painted by hand; every
# other body gets it moved to its own UV layout. A file whose bodies are there and whose moved
# paint is current is left as it is. Run it again after painting, so the other bodies show the
# new paint; then the file needs a new review and approval. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Rebuilds the bodies even when the file is current.
    [switch]$Force,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset) -AllowMissingSource
if ($manifest.Kind -ne "paint") {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a paint layer is synced from its bodies."
}

$arguments = @(
    "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
    "--python", $script:ArtBlenderCli, "--", "paint-sync",
    "--file", $manifest.SourcePath, "--paint", $manifest.Name, "--part", $manifest.Part)
foreach ($body in @($manifest.Bodies)) {
    $item = Get-Item -LiteralPath $body.SourcePath
    if ($item.Length -lt 1024 -and [System.IO.File]::ReadAllText($item.FullName).StartsWith("version https://git-lfs")) {
        throw "$($body.SourcePath) is a Git LFS pointer; run git lfs pull first."
    }

    $arguments += @("--body", "$($body.SourcePath)=$($body.Root)")
}

if ($Force) {
    $arguments += "--force"
}

$blender = Resolve-ArtBlender $BlenderPath
$output = Invoke-ArtNative $blender $arguments
$errors = @($output | Where-Object { $_ -like "ART_PIPELINE_ERROR *" } |
    ForEach-Object { $_.Substring("ART_PIPELINE_ERROR ".Length) })
if ($errors.Count -gt 0) {
    throw "$($manifest.Name) cannot be synced:`n - $($errors -join "`n - ")"
}

$result = @($output | Where-Object { $_ -like "ART_PAINT_SYNC *" }) | Select-Object -First 1
if ($script:ArtNativeExitCode -ne 0 -or $null -eq $result) {
    throw "Blender failed with exit code $script:ArtNativeExitCode for $($manifest.Name):`n$(($output | Select-Object -Last 30) -join "`n")"
}

if ($result -like "ART_PAINT_SYNC up-to-date *") {
    Write-Host "$($manifest.Name) is up to date: every body is there and shows the current paint."
}
else {
    Write-Host "Synced $($manifest.Name): $($manifest.SourcePath)"
    Write-Host "Review the paint on every body, then record the approval with tools/Approve-ArtAsset.ps1 -Stage paint."
}

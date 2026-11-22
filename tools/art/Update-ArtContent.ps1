# Makes the content records of art assets current after a .blend file was saved by hand: reads the
# content of each file with Blender when its record was read from another version of the file, writes
# it to asset.json, and prints which approvals hold. Every script that saves a .blend file does this
# itself. See docs/ART_PIPELINE.md.
param(
    [string[]]$Asset,
    # Every asset below art/.
    [switch]$All,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

if ($All -eq ($Asset.Count -gt 0)) {
    throw "Pass either -Asset <folder> or -All."
}

$directories = if ($All) { Get-ArtAssetDirectories } else { @($Asset | ForEach-Object { Resolve-ArtAssetDirectory $_ }) }
$blender = Resolve-ArtBlender $BlenderPath
foreach ($directory in $directories) {
    $manifest = Read-ArtAssetManifest $directory
    $changed = Update-ArtContentRecord $blender $manifest
    $state = if ($changed) { "recorded its content" } else { "its content record is current" }
    Write-Host "$($manifest.RelativeDirectory): $state."
    foreach ($line in @(Get-ArtStageReport $manifest)) {
        Write-Host "  $line"
    }
}

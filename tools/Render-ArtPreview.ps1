# Renders the review sheet of an art asset: one row per exported root object, seen from four
# sides at the game camera pitch, plus a view with a 1.8 m mannequin for scale. The sheet of an
# animation library shows every clip as frames on its preview bodies instead. The sheet is
# what a reviewer looks at before Approve-ArtAsset.ps1. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # Defaults to artifacts/art-previews/<Name>.png, which Git ignores.
    [string]$Output,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews/$($manifest.Name).png"
}

$Output = [System.IO.Path]::GetFullPath($Output)
$blender = Resolve-ArtBlender $BlenderPath
Invoke-ArtBlender $blender $manifest @("preview", "--output", $Output, "--kind", $manifest.Kind) | Out-Null
Write-Host "Review sheet for $($manifest.Name): $Output"

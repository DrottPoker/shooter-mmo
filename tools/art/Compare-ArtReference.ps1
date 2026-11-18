# Measures how closely a static or skinned model follows its reference pictures, the image empties of
# the Reference collection in its .blend file: each picture is seen by an orthographic camera as the
# picture faces, the model's silhouette is rendered from there, and per picture the script prints the
# overlap of both figures and how much of the picture's figure the model misses and adds outside it,
# with an overlay image below artifacts/art-previews. It is a measure for the reviewer,
# not a gate: it never fails on a number, and it never saves the file. See docs/ART_PIPELINE.md.
param(
    [Parameter(Mandatory = $true)]
    [string]$Asset,
    # The root object whose meshes are compared; the manifest's first root object by default.
    [string]$Root,
    [string]$BlenderPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ArtPipelineCommon.ps1")

$manifest = Read-ArtAssetManifest (Resolve-ArtAssetDirectory $Asset)
if (@("static", "skinned") -cnotcontains $manifest.Kind) {
    throw "$($manifest.Name) is a $($manifest.Kind) asset; only a static or skinned model is compared with its reference pictures."
}

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = $manifest.RootObjects[0]
}
elseif ($manifest.RootObjects -cnotcontains $Root) {
    throw "$Root is not a root object of $($manifest.Name); its root objects are $($manifest.RootObjects -join ', ')."
}

$output = Join-Path $script:ArtRepositoryRoot "artifacts/art-previews"
$blender = Resolve-ArtBlender $BlenderPath
$results = Invoke-ArtBlender $blender $manifest @("reference", "--output", $output, "--root", $Root)
$culture = [System.Globalization.CultureInfo]::InvariantCulture
foreach ($line in @($results | Where-Object { $_ -like "ART_REFERENCE *" })) {
    $view = $line.Substring("ART_REFERENCE ".Length) | ConvertFrom-Json
    # Both shares are of the picture's figure.
    Write-Host ("{0}: {1} % overlap; the model misses {2} % of the picture's figure and adds {3} % outside it: {4}" -f
        $view.view, ([double]$view.overlap * 100).ToString("0.0", $culture), ([double]$view.pictureOnly * 100).ToString("0.0", $culture),
        ([double]$view.modelOnly * 100).ToString("0.0", $culture), $view.overlay)
}
